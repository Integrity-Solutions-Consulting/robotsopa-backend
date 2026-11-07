using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>
/// Transactional PostgreSQL implementation of the legacy state contract.
/// </summary>
public sealed class PostgresStateRepository(string connectionString) : IStateRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public T Read<T>(Func<State, T> read)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id = 1", connection);
        return read(Deserialize(command.ExecuteScalar()));
    }

    public T Update<T>(Func<State, T> mutate)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var select = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id = 1 FOR UPDATE", connection, transaction);
        var state = Deserialize(select.ExecuteScalar());
        var accountsBefore = state.Accounts.ToList();
        using var active = new NpgsqlCommand("SELECT job_id FROM execution_ownership WHERE coordination_state='leased'", connection, transaction);
        using var reader = active.ExecuteReader();
        var leasedIds = new List<string>();
        while (reader.Read()) leasedIds.Add(reader.GetString(0));
        reader.Close();
        var before = leasedIds.ToDictionary(id => id, id => state.Executions.SingleOrDefault(e => e.Id == id) is { } e ? JsonSerializer.Serialize(e, Options) : null);
        var result = mutate(state);
        foreach (var id in leasedIds)
        {
            var job = state.Executions.SingleOrDefault(e => e.Id == id);
            if (job is null || before[id] is null) throw new InvalidOperationException("Cannot remove a leased execution.");
            var previous = JsonSerializer.Deserialize<SriAutomation.Domain.Execution>(before[id]!, Options)!;
            var requested = job.CancelRequested;
            job.CancelRequested = previous.CancelRequested;
            var unchanged = JsonSerializer.Serialize(job, Options) == before[id];
            job.CancelRequested = requested;
            if (!unchanged || (previous.CancelRequested && !requested)) throw new InvalidOperationException("A leased execution requires its ownership token.");
        }
        PostgresPanelDatabase.ApplyAccountChanges(connection, transaction, accountsBefore, state.Accounts);
        PostgresPanelDatabase.SyncAudit(connection, transaction, state.Audit);
        using var update = new NpgsqlCommand("UPDATE app_state SET payload = @payload::jsonb WHERE id = 1", connection, transaction);
        update.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(state, Options));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("PostgreSQL state row is missing.");
        transaction.Commit();
        return result;
    }

    public T UpdateOwned<T>(ExecutionOwnership ownership, Func<State, T> mutate)
    {
        using var connection = new NpgsqlConnection(connectionString); connection.Open();
        using var transaction = connection.BeginTransaction();
        using var select = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id=1 FOR UPDATE", connection, transaction);
        var state = Deserialize(select.ExecuteScalar());
        var accountsBefore = state.Accounts.ToList();
        using var check = new NpgsqlCommand("SELECT 1 FROM execution_ownership WHERE job_id=@job AND worker_id=@worker AND lease_id=@lease AND fencing_token=@token AND coordination_state='leased' AND lease_expires_at>clock_timestamp() FOR UPDATE", connection, transaction);
        check.Parameters.AddWithValue("job", ownership.JobId); check.Parameters.AddWithValue("worker", ownership.WorkerId); check.Parameters.AddWithValue("lease", ownership.LeaseId); check.Parameters.AddWithValue("token", ownership.FencingToken);
        if (check.ExecuteScalar() is null) throw new InvalidOperationException("Execution ownership has expired or changed.");
        var otherJobs = state.Executions.Where(e => e.Id != ownership.JobId).ToDictionary(e => e.Id, e => JsonSerializer.Serialize(e, Options));
        var ownedBefore = state.Executions.SingleOrDefault(e => e.Id == ownership.JobId) ?? throw new InvalidOperationException("Execution is missing.");
        var cancellationRequested = ownedBefore.CancelRequested;
        var originalQuery = JsonSerializer.Serialize(ownedBefore.Query, Options);
        var result = mutate(state);
        if (state.Executions.Count(e => e.Id == ownership.JobId) != 1 || state.Executions.Count != otherJobs.Count + 1 || state.Executions.Where(e => e.Id != ownership.JobId).Any(e => !otherJobs.TryGetValue(e.Id, out var prior) || JsonSerializer.Serialize(e, Options) != prior))
            throw new InvalidOperationException("Ownership cannot alter other executions.");
        var ownedAfter = state.Executions.Single(e => e.Id == ownership.JobId);
        if ((cancellationRequested && !ownedAfter.CancelRequested) || JsonSerializer.Serialize(ownedAfter.Query, Options) != originalQuery)
            throw new InvalidOperationException("Ownership cannot clear cancellation or change the execution query.");
        PostgresPanelDatabase.ApplyAccountChanges(connection, transaction, accountsBefore, state.Accounts);
        PostgresPanelDatabase.SyncAudit(connection, transaction, state.Audit);
        using var update = new NpgsqlCommand("UPDATE app_state SET payload=@payload::jsonb WHERE id=1", connection, transaction);
        update.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(state, Options));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("PostgreSQL state row is missing.");
        if (check.ExecuteScalar() is null) throw new InvalidOperationException("Execution ownership expired during the update.");
        EnqueueOwnedFiles(connection, transaction, state, ownedAfter);
        if (check.ExecuteScalar() is null) throw new InvalidOperationException("Execution ownership expired while queueing deliveries.");
        transaction.Commit(); return result;
    }

    private static void EnqueueOwnedFiles(NpgsqlConnection connection, NpgsqlTransaction transaction, State state, Execution job)
    {
        if (string.IsNullOrWhiteSpace(job.RequestedBy)) return;
        var account = state.Accounts.SingleOrDefault(a => a.Id == job.Query.Account);
        var taxId = account?.TaxId ?? account?.Id;
        if (taxId is null || taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) return;
        void Enqueue(string id, string kind, string? source, string? hash, string destination)
        {
            if (source is null || hash is null || hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) return;
            var parts = source.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != ".objects" || parts[1] != hash[..2] || parts[2] != hash + "." + kind) return;
            using var command = new NpgsqlCommand("""
                INSERT INTO remote_deliveries(id,username,account_id,document_id,kind,source_path,destination_path,sha256)
                SELECT @id,u.username,a.id,@document,@kind,@source,@destination,@hash
                FROM panel_users u JOIN panel_accounts a ON a.id=@account AND a.active
                WHERE u.username=@username AND u.password_hash IS NOT NULL
                  AND (u.role='admin' OR EXISTS (SELECT 1 FROM panel_assignments x WHERE x.username=u.username AND x.account_id=a.id))
                ON CONFLICT(username,document_id,kind,sha256) DO NOTHING
                """, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("username", job.RequestedBy);
            command.Parameters.AddWithValue("account", job.Query.Account); command.Parameters.AddWithValue("document", id);
            command.Parameters.AddWithValue("kind", kind); command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("destination", destination); command.Parameters.AddWithValue("hash", hash);
            command.ExecuteNonQuery();
        }
        foreach (var document in state.Documents.Where(d => d.Account == job.Query.Account && d.Issued >= job.Query.From && d.Issued <= job.Query.To && job.Query.Types.Contains(d.Type)))
        {
            if (!Validation.Types.Contains(document.Type)) continue;
            var key = Validation.Identifier(document.AccessKey);
            var prefix = $"{taxId}/{document.Issued.Year:D4}/{document.Issued.Month:D2}/{document.Type}/{key}";
            Enqueue(document.Id, "xml", document.XmlPath, document.XmlSha256, prefix + ".xml");
            Enqueue(document.Id, "pdf", document.PdfPath, document.PdfSha256, prefix + ".pdf");
        }
        foreach (var report in job.Reports)
        {
            var filename = Path.GetFileNameWithoutExtension(report.Path);
            Enqueue($"report-{job.Query.Account}-{report.Year:D4}-{report.Month:D2}", "xlsx", report.Path, filename,
                $"{taxId}/{report.Year:D4}/{report.Month:D2}/Reportes/Resumen_{report.Year:D4}_{report.Month:D2}.xlsx");
        }
    }

    private static State Deserialize(object? value)
    {
        if (value is not string json) throw new InvalidOperationException("PostgreSQL state row is missing; initialize the database first.");
        return JsonSerializer.Deserialize<State>(json, Options) ?? throw new InvalidDataException("PostgreSQL state is invalid.");
    }
}

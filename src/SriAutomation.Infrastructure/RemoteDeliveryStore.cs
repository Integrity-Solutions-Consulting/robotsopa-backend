using Npgsql;

namespace SriAutomation.Infrastructure;

public sealed record PendingRemoteDelivery(Guid Id, string AccountId, string DocumentId, string Kind, string DestinationPath, string Sha256, int Attempts);
public sealed record RemoteDeliveryFile(string AccountId, string DocumentId, string Kind, string SourcePath, string Sha256);

/// <summary>Access to deliveries is checked against current user assignments in PostgreSQL.</summary>
public sealed class RemoteDeliveryStore(string connectionString)
{
    private const string Authorized = """
        d.username=@username AND EXISTS (
          SELECT 1 FROM panel_users u JOIN panel_accounts a ON a.id=d.account_id AND a.active
          WHERE u.username=d.username AND u.password_hash IS NOT NULL
          AND (u.role='admin' OR EXISTS (SELECT 1 FROM panel_assignments x WHERE x.username=u.username AND x.account_id=a.id)))
        """;

    public bool Enqueue(string username, string accountId, string documentId, string kind, string source, string destination, string hash)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO remote_deliveries(id,username,account_id,document_id,kind,source_path,destination_path,sha256)
            SELECT @id,u.username,a.id,@document,@kind,@source,@destination,@hash FROM panel_users u JOIN panel_accounts a ON a.id=@account AND a.active
            WHERE u.username=@username AND u.password_hash IS NOT NULL
              AND (u.role='admin' OR EXISTS (SELECT 1 FROM panel_assignments x WHERE x.username=u.username AND x.account_id=a.id))
            ON CONFLICT(username,document_id,kind,sha256) DO NOTHING
            """, db);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid()); cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("account", accountId); cmd.Parameters.AddWithValue("document", documentId); cmd.Parameters.AddWithValue("kind", kind); cmd.Parameters.AddWithValue("source", source); cmd.Parameters.AddWithValue("destination", destination); cmd.Parameters.AddWithValue("hash", hash);
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<PendingRemoteDelivery> Pending(string username)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand($"SELECT d.id,d.account_id,d.document_id,d.kind,d.destination_path,d.sha256,d.attempts FROM remote_deliveries d WHERE {Authorized} AND d.status='Pending' ORDER BY d.created_at,d.id LIMIT 100", db);
        cmd.Parameters.AddWithValue("username", username);
        using var reader = cmd.ExecuteReader(); var result = new List<PendingRemoteDelivery>();
        while (reader.Read()) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
        return result;
    }

    public bool Confirm(string username, Guid id, string hash)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand($"UPDATE remote_deliveries d SET status='Delivered',delivered_at=COALESCE(delivered_at,now()),last_error=NULL WHERE {Authorized} AND d.id=@id AND d.sha256=@hash AND d.status IN ('Pending','Delivered')", db);
        cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("hash", hash);
        return cmd.ExecuteNonQuery() == 1;
    }

    public RemoteDeliveryFile? GetFile(string username, Guid id)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand($"SELECT d.account_id,d.document_id,d.kind,d.source_path,d.sha256 FROM remote_deliveries d WHERE {Authorized} AND d.id=@id", db);
        cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    public bool RecordFailure(string username, Guid id, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 64 || errorCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new ArgumentException("Invalid error code.");
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand($"UPDATE remote_deliveries d SET attempts=attempts+1,last_error=@error WHERE {Authorized} AND d.id=@id AND d.status='Pending'", db);
        cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("error", errorCode);
        return cmd.ExecuteNonQuery() == 1;
    }
}

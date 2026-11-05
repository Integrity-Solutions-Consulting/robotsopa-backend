using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>Coordinates a specific job. State and ownership always lock in that order.</summary>
public sealed class PostgresExecutionCoordinator(string connectionString)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public ExecutionOwnership? Acquire(string jobId, string workerId, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(workerId) || duration <= TimeSpan.Zero) throw new ArgumentException("Invalid acquisition arguments.");
        using var db = new NpgsqlConnection(connectionString); db.Open(); using var tx = db.BeginTransaction();
        using var stateCommand = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id=1 FOR UPDATE", db, tx);
        var state = JsonSerializer.Deserialize<State>((string?)stateCommand.ExecuteScalar() ?? throw new InvalidOperationException("Missing state."), Options) ?? throw new InvalidOperationException("Invalid state.");
        var job = state.Executions.SingleOrDefault(e => e.Id == jobId);
        if (job is null || job.CancelRequested || job.Status is not ("Pending" or "Running")) return null;
        if (job.Status == "Running")
        {
            using var expired = new NpgsqlCommand("SELECT 1 FROM execution_ownership WHERE job_id=@job AND (coordination_state='released' OR lease_expires_at<=clock_timestamp())", db, tx);
            expired.Parameters.AddWithValue("job", jobId);
            if (expired.ExecuteScalar() is null) return null;
        }
        var lease = Guid.NewGuid();
        using var command = new NpgsqlCommand("""
            INSERT INTO execution_ownership(job_id,worker_id,lease_id,fencing_token,lease_expires_at,coordination_state,attempts,acquired_at,renewed_at,updated_at)
            VALUES (@job,@worker,@lease,1,clock_timestamp()+@duration,'leased',1,clock_timestamp(),clock_timestamp(),clock_timestamp())
            ON CONFLICT(job_id) DO UPDATE SET worker_id=excluded.worker_id,lease_id=excluded.lease_id,
              fencing_token=execution_ownership.fencing_token+1,lease_expires_at=clock_timestamp()+@duration,
              coordination_state='leased',attempts=execution_ownership.attempts+1,acquired_at=clock_timestamp(),renewed_at=clock_timestamp(),updated_at=clock_timestamp()
            WHERE execution_ownership.coordination_state='released' OR execution_ownership.lease_expires_at<=clock_timestamp()
            RETURNING fencing_token
            """, db, tx);
        command.Parameters.AddWithValue("job", jobId); command.Parameters.AddWithValue("worker", workerId); command.Parameters.AddWithValue("lease", lease); command.Parameters.AddWithValue("duration", duration);
        var token = command.ExecuteScalar(); if (token is not long value) return null;
        job.Status = "Running"; job.Started ??= DateTimeOffset.UtcNow; job.Attempts++;
        using var update = new NpgsqlCommand("UPDATE app_state SET payload=@payload::jsonb WHERE id=1", db, tx);
        update.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(state, Options)); update.ExecuteNonQuery();
        tx.Commit(); return new ExecutionOwnership(jobId, workerId, lease, value);
    }

    public bool Renew(ExecutionOwnership ownership, TimeSpan duration) => Change(ownership, duration, false);
    public bool Release(ExecutionOwnership ownership) => Change(ownership, TimeSpan.Zero, true);

    /// <summary>Finalizes cancellations only after the current lease can no longer write.</summary>
    public int FinalizeAbandonedCancellations()
    {
        using var db = new NpgsqlConnection(connectionString); db.Open(); using var tx = db.BeginTransaction();
        using var select = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id=1 FOR UPDATE", db, tx);
        var state = JsonSerializer.Deserialize<State>((string?)select.ExecuteScalar() ?? throw new InvalidOperationException("Missing state."), Options) ?? throw new InvalidOperationException("Invalid state.");
        var count = 0;
        foreach (var job in state.Executions.Where(e => e.Status == "Running" && e.CancelRequested))
        {
            using var expired = new NpgsqlCommand("UPDATE execution_ownership SET coordination_state='released',updated_at=clock_timestamp() WHERE job_id=@job AND (coordination_state='released' OR lease_expires_at<=clock_timestamp()) RETURNING job_id", db, tx);
            expired.Parameters.AddWithValue("job", job.Id);
            if (expired.ExecuteScalar() is null)
            {
                using var exists = new NpgsqlCommand("SELECT 1 FROM execution_ownership WHERE job_id=@job", db, tx);
                exists.Parameters.AddWithValue("job", job.Id);
                if (exists.ExecuteScalar() is not null) continue;
            }
            job.Status = job.Stage = "Cancelled";
            job.Message = "Cancelada tras finalizar el lease anterior.";
            job.Finished = DateTimeOffset.UtcNow;
            count++;
        }
        if (count > 0)
        {
            using var update = new NpgsqlCommand("UPDATE app_state SET payload=@payload::jsonb WHERE id=1", db, tx);
            update.Parameters.AddWithValue("payload", NpgsqlDbType.Text, JsonSerializer.Serialize(state, Options));
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Missing state.");
        }
        tx.Commit();
        return count;
    }

    private bool Change(ExecutionOwnership ownership, TimeSpan duration, bool release)
    {
        if (!release && duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand(release
            ? "UPDATE execution_ownership SET coordination_state='released',lease_expires_at=clock_timestamp(),updated_at=clock_timestamp() WHERE job_id=@job AND worker_id=@worker AND lease_id=@lease AND fencing_token=@token AND coordination_state='leased' AND lease_expires_at>clock_timestamp()"
            : "UPDATE execution_ownership SET lease_expires_at=clock_timestamp()+@duration,renewed_at=clock_timestamp(),updated_at=clock_timestamp() WHERE job_id=@job AND worker_id=@worker AND lease_id=@lease AND fencing_token=@token AND coordination_state='leased' AND lease_expires_at>clock_timestamp()", db);
        cmd.Parameters.AddWithValue("job", ownership.JobId); cmd.Parameters.AddWithValue("worker", ownership.WorkerId); cmd.Parameters.AddWithValue("lease", ownership.LeaseId); cmd.Parameters.AddWithValue("token", ownership.FencingToken);
        if (!release) cmd.Parameters.AddWithValue("duration", duration);
        return cmd.ExecuteNonQuery() == 1;
    }
}

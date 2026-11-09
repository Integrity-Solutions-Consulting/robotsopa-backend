using Npgsql;
using NpgsqlTypes;

namespace SriAutomation.Infrastructure;

public sealed record ZipJobLease(Guid JobId, Guid InstanceId, Guid LeaseId, long Generation);
public sealed record StoredZipJob(Guid Id, string User, string Account, string Selection, string Signature,
    string Status, int Expected, int Processed, int Omitted, string Issues, string Warnings,
    string? ArchiveName, string? ArchiveSha256, long? ArchiveSize, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? InstanceId, DateTimeOffset? LeaseExpiresAt = null);

/// <summary>PostgreSQL state and fencing for ZIP jobs; archive bytes never enter the database.</summary>
public interface IZipJobStore
{
    void Create(Guid id, string user, string account, string selectionJson, string signature, int expected, DateTimeOffset expiresAt);
    ZipJobLease? Claim(Guid id, Guid instance, TimeSpan duration);
    bool Renew(ZipJobLease lease, TimeSpan duration);
    bool Progress(ZipJobLease lease, int processed, int omitted);
    bool Finish(ZipJobLease lease, string status, string? archiveName, string? sha256, long? size, string issuesJson, string warningsJson);
    StoredZipJob? Find(Guid id, string user, string account);
    StoredZipJob? FindForProcessing(Guid id);
    StoredZipJob? FindReusable(string user, string account, string signature);
    bool Authorized(string user, string account);
    void Expire();
    IReadOnlyList<Guid> Recoverable(int limit = 100);
}
public sealed class PostgresZipJobStore(string connectionString) : IZipJobStore
{
    public void Create(Guid id, string user, string account, string selectionJson, string signature, int expected, DateTimeOffset expiresAt)
    {
        if (expected is < 0 or > 2000 || expiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Invalid ZIP job limits.");
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("""
            INSERT INTO document_zip_jobs(id,username,account_id,selection,signature,status,expected,expires_at)
            VALUES (@id,@user,@account,@selection::jsonb,@signature,'Pending',@expected,@expires)
            """, db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("user", user); cmd.Parameters.AddWithValue("account", account);
        cmd.Parameters.AddWithValue("selection", NpgsqlDbType.Text, selectionJson); cmd.Parameters.AddWithValue("signature", signature);
        cmd.Parameters.AddWithValue("expected", expected); cmd.Parameters.AddWithValue("expires", expiresAt); cmd.ExecuteNonQuery();
    }
    public ZipJobLease? Claim(Guid id, Guid instance, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)) throw new ArgumentException("Invalid lease duration.");
        var lease = Guid.NewGuid(); using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("""
            UPDATE document_zip_jobs SET status='Preparing',instance_id=@instance,lease_id=@lease,
                lease_generation=lease_generation+1,lease_expires_at=clock_timestamp()+@duration,
                processed=0,omitted=0,archive_name=NULL,archive_sha256=NULL,archive_size=NULL,updated_at=clock_timestamp()
            WHERE id=@id AND expires_at>clock_timestamp() AND
                (status='Pending' OR (status='Preparing' AND lease_expires_at<=clock_timestamp()))
            RETURNING lease_generation
            """, db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("instance", instance); cmd.Parameters.AddWithValue("lease", lease); cmd.Parameters.AddWithValue("duration", duration);
        return cmd.ExecuteScalar() is long generation ? new(id, instance, lease, generation) : null;
    }
    public bool Renew(ZipJobLease lease, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30)) throw new ArgumentException("Invalid lease duration.");
        return Mutate(lease, "UPDATE document_zip_jobs SET lease_expires_at=clock_timestamp()+@duration,updated_at=clock_timestamp() WHERE " + Ownership, cmd => cmd.Parameters.AddWithValue("duration", duration));
    }
    public bool Progress(ZipJobLease lease, int processed, int omitted)
    {
        if (processed < 0 || omitted < 0 || omitted > processed) throw new ArgumentException("Invalid progress.");
        return Mutate(lease, "UPDATE document_zip_jobs SET processed=@processed,omitted=@omitted,updated_at=clock_timestamp() WHERE " + Ownership + " AND @processed<=expected", cmd => { cmd.Parameters.AddWithValue("processed", processed); cmd.Parameters.AddWithValue("omitted", omitted); });
    }
    public bool Finish(ZipJobLease lease, string status, string? archiveName, string? sha256, long? size, string issuesJson, string warningsJson)
    {
        if (status is not ("Ready" or "Incomplete" or "Failed")) throw new ArgumentException("Invalid status.");
        if (status == "Ready" && (archiveName != lease.JobId.ToString("N") + "-" + lease.LeaseId.ToString("N") + ".zip" || sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || size is null or <= 0 or > 536870912)) throw new ArgumentException("Invalid ZIP metadata.");
        if (status != "Ready" && (archiveName is not null || sha256 is not null || size is not null)) throw new ArgumentException("Non-ready ZIP cannot be published.");
        return Mutate(lease, """
            UPDATE document_zip_jobs SET status=@status,archive_name=@name,archive_sha256=@hash,archive_size=@size,
                issues=@issues::jsonb,warnings=@warnings::jsonb,lease_id=NULL,lease_expires_at=NULL,updated_at=clock_timestamp()
            WHERE
            """ + " " + Ownership, cmd => {
                cmd.Parameters.AddWithValue("status", status); cmd.Parameters.AddWithValue("name", NpgsqlDbType.Text, (object?)archiveName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("hash", NpgsqlDbType.Text, (object?)sha256 ?? DBNull.Value); cmd.Parameters.AddWithValue("size", NpgsqlDbType.Bigint, (object?)size ?? DBNull.Value);
                cmd.Parameters.AddWithValue("issues", NpgsqlDbType.Text, issuesJson); cmd.Parameters.AddWithValue("warnings", NpgsqlDbType.Text, warningsJson);
            });
    }
    private const string Ownership = "id=@id AND status='Preparing' AND instance_id=@instance AND lease_id=@lease AND lease_generation=@generation AND lease_expires_at>clock_timestamp() AND expires_at>clock_timestamp()";
    private bool Mutate(ZipJobLease lease, string sql, Action<NpgsqlCommand> parameters)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open(); using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", lease.JobId); cmd.Parameters.AddWithValue("instance", lease.InstanceId);
        cmd.Parameters.AddWithValue("lease", lease.LeaseId); cmd.Parameters.AddWithValue("generation", lease.Generation);
        parameters(cmd); return cmd.ExecuteNonQuery() == 1;
    }
    public StoredZipJob? Find(Guid id, string user, string account)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("""
            SELECT id,username,account_id,selection::text,signature,status,expected,processed,omitted,issues::text,warnings::text,
                   archive_name,archive_sha256,archive_size,created_at,expires_at,instance_id,lease_expires_at
            FROM document_zip_jobs WHERE id=@id AND username=@user AND account_id=@account AND expires_at>clock_timestamp()
            """, db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("user", user); cmd.Parameters.AddWithValue("account", account);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetString(9),r.GetString(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),r.IsDBNull(13)?null:r.GetInt64(13),r.GetFieldValue<DateTimeOffset>(14),r.GetFieldValue<DateTimeOffset>(15),r.IsDBNull(16)?null:r.GetGuid(16),r.IsDBNull(17)?null:r.GetFieldValue<DateTimeOffset>(17));
    }
    public StoredZipJob? FindForProcessing(Guid id)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT username,account_id FROM document_zip_jobs WHERE id=@id AND expires_at>clock_timestamp()", db);
        cmd.Parameters.AddWithValue("id", id); using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var user = reader.GetString(0); var account = reader.GetString(1); reader.Close();
        return Find(id, user, account);
    }
    public StoredZipJob? FindReusable(string user, string account, string signature)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT id FROM document_zip_jobs WHERE username=@user AND account_id=@account AND signature=@signature AND status IN ('Pending','Preparing','Ready') AND expires_at>clock_timestamp() ORDER BY created_at DESC LIMIT 1", db);
        cmd.Parameters.AddWithValue("user", user); cmd.Parameters.AddWithValue("account", account); cmd.Parameters.AddWithValue("signature", signature);
        return cmd.ExecuteScalar() is Guid id ? Find(id, user, account) : null;
    }
    public bool Authorized(string user, string account)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM panel_users u JOIN panel_accounts a ON a.id=@account AND a.active
              WHERE u.username=@user AND u.password_hash IS NOT NULL AND
                (u.role='admin' OR EXISTS(SELECT 1 FROM panel_assignments x WHERE x.username=u.username AND x.account_id=a.id)))
            """, db);
        cmd.Parameters.AddWithValue("user", user); cmd.Parameters.AddWithValue("account", account);
        return cmd.ExecuteScalar() is true;
    }
    public void Expire()
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("UPDATE document_zip_jobs SET status='Expired',lease_id=NULL,lease_expires_at=NULL,updated_at=clock_timestamp() WHERE expires_at<=clock_timestamp() AND status<>'Expired'", db);
        cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<Guid> Recoverable(int limit = 100)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT id FROM document_zip_jobs WHERE expires_at>clock_timestamp() AND (status='Pending' OR (status='Preparing' AND lease_expires_at<=clock_timestamp())) ORDER BY created_at LIMIT @limit", db);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100)); using var r = cmd.ExecuteReader(); var ids = new List<Guid>(); while (r.Read()) ids.Add(r.GetGuid(0)); return ids;
    }
}

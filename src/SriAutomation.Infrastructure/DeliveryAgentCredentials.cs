using System.Security.Cryptography;
using Npgsql;

namespace SriAutomation.Infrastructure;

/// <summary>Stores only hashes of individually revocable, random agent credentials.</summary>
public sealed class DeliveryAgentCredentials(string connectionString)
{
    public IReadOnlyList<DeliveryAgentInfo> List(string username)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT id,label,created_at,revoked_at,last_seen_at FROM delivery_agents WHERE username=@username ORDER BY created_at DESC", db);
        cmd.Parameters.AddWithValue("username", username);
        using var reader = cmd.ExecuteReader();
        var result = new List<DeliveryAgentInfo>();
        while (reader.Read()) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTime>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3), reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTime>(4)));
        return result;
    }
    public (Guid Id, string Secret) Register(string username, string label)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(label) || label.Length > 100) throw new ArgumentException("Invalid agent registration.");
        var id = Guid.NewGuid();
        var bytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToHexString(bytes);
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO delivery_agents(id,username,label,secret_hash) SELECT @id,username,@label,@hash FROM panel_users WHERE lower(username)=lower(@username) AND password_hash IS NOT NULL", db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("label", label.Trim()); cmd.Parameters.AddWithValue("hash", SHA256.HashData(bytes));
        if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("User does not exist or is inactive.");
        CryptographicOperations.ZeroMemory(bytes);
        return (id, secret);
    }

    public string? Authenticate(Guid id, string secret)
    {
        byte[] supplied;
        try { supplied = Convert.FromHexString(secret); }
        catch (FormatException) { return null; }
        if (supplied.Length != 32) return null;
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT a.username,a.secret_hash FROM delivery_agents a JOIN panel_users u ON u.username=a.username WHERE a.id=@id AND a.revoked_at IS NULL AND u.password_hash IS NOT NULL", db);
        cmd.Parameters.AddWithValue("id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(supplied), (byte[])reader[1]) ? reader.GetString(0) : null;
    }

    public void RecordExecutionActivity(Guid id, string username)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("UPDATE delivery_agents SET last_seen_at=now() WHERE id=@id AND username=@username AND revoked_at IS NULL", db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("username", username);
        cmd.ExecuteNonQuery();
    }

    public bool Revoke(Guid id, string username)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("UPDATE delivery_agents SET revoked_at=now() WHERE id=@id AND lower(username)=lower(@username) AND revoked_at IS NULL", db);
        cmd.Parameters.AddWithValue("id", id); cmd.Parameters.AddWithValue("username", username);
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool CanAccess(string username, string account)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM panel_users u JOIN panel_accounts a ON a.id=@account AND a.active WHERE u.username=@username AND u.password_hash IS NOT NULL AND (u.role='admin' OR EXISTS(SELECT 1 FROM panel_assignments x WHERE x.username=u.username AND x.account_id=a.id)))", db);
        cmd.Parameters.AddWithValue("username", username); cmd.Parameters.AddWithValue("account", account);
        return cmd.ExecuteScalar() is true;
    }
}

public sealed record DeliveryAgentInfo(Guid Id, string Label, DateTime CreatedAt, DateTime? RevokedAt, DateTime? LastSeenAt);

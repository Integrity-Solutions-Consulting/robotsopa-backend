using System.Security.Cryptography;
using Npgsql;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>Panel storage for PostgreSQL.</summary>
public sealed class PostgresPanelDatabase(string connectionString) : IPanelDatabase
{
    private NpgsqlConnection Open() { var connection = new NpgsqlConnection(connectionString); connection.Open(); return connection; }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    public (string User, string Role, bool MustChange)? Authenticate(string username, string password)
    {
        using var db = Open();
        using var cmd = new NpgsqlCommand("SELECT username,role,salt,password_hash,must_change FROM panel_users WHERE lower(username)=lower(@user)", db);
        cmd.Parameters.AddWithValue("user", username.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2) || reader.IsDBNull(3)) return null;
        return CryptographicOperations.FixedTimeEquals(Hash(password, (byte[])reader[2]), (byte[])reader[3])
            ? (reader.GetString(0), reader.GetString(1), reader.GetBoolean(4)) : null;
    }

    public void Initialize(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 80 || username.Any(char.IsControl)) throw new ArgumentException("Usuario inválido.");
        if (password is null) throw new ArgumentException("La contraseña de inicialización está ausente.", nameof(password));
        if (password.Length is < 16 or > 1024) throw new ArgumentException($"La contraseña de inicialización tiene una longitud inválida ({password.Length}); debe tener entre 16 y 1024 unidades UTF-16.", nameof(password));
        var salt = RandomNumberGenerator.GetBytes(16);
        using var db = Open();
        using var cmd = new NpgsqlCommand("INSERT INTO panel_users(username,role,salt,password_hash) VALUES (@user,'admin',@salt,@hash) ON CONFLICT DO NOTHING", db);
        cmd.Parameters.AddWithValue("user", username.Trim()); cmd.Parameters.AddWithValue("salt", salt); cmd.Parameters.AddWithValue("hash", Hash(password, salt));
        cmd.ExecuteNonQuery();
    }

    public HashSet<string> AssignedAccounts(string username)
    {
        using var db = Open();
        using var cmd = new NpgsqlCommand("SELECT a.account_id FROM panel_assignments a JOIN panel_accounts r ON r.id=a.account_id WHERE lower(a.username)=lower(@user) AND r.active", db);
        cmd.Parameters.AddWithValue("user", username.Trim());
        using var reader = cmd.ExecuteReader(); var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public object ListUsers()
    {
        using var db = Open(); using var cmd = new NpgsqlCommand("SELECT username,role,password_hash IS NOT NULL FROM panel_users ORDER BY username", db);
        using var reader = cmd.ExecuteReader(); var users = new List<object>();
        while (reader.Read()) users.Add(new { username = reader.GetString(0), role = reader.GetString(1), active = reader.GetBoolean(2) });
        return users;
    }

    public void SaveUser(string username, string role, string password)
    {
        username = username?.Trim() ?? "";
        if (username.Length is < 2 or > 80 || username.Any(char.IsControl)) throw new ArgumentException("Usuario inválido.");
        if (role is not "admin" and not "asistente") throw new ArgumentException("Rol inválido.");
        if (password is null || password.Length is < 12 or > 1024) throw new ArgumentException("Contraseña inválida.");
        var salt = RandomNumberGenerator.GetBytes(16);
        using var db = Open(); using var cmd = new NpgsqlCommand("INSERT INTO panel_users(username,role,salt,password_hash,must_change) VALUES(@user,@role,@salt,@hash,true) ON CONFLICT (lower(username)) DO UPDATE SET salt=excluded.salt,password_hash=excluded.password_hash,must_change=true WHERE panel_users.role=excluded.role", db);
        cmd.Parameters.AddWithValue("user", username); cmd.Parameters.AddWithValue("role", role); cmd.Parameters.AddWithValue("salt", salt); cmd.Parameters.AddWithValue("hash", Hash(password, salt));
        if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("El usuario ya existe con otro rol.");
    }

    public void ChangePassword(string username, string currentPassword, string newPassword)
    {
        if (Authenticate(username, currentPassword) is null) throw new ArgumentException("Contraseña actual incorrecta.");
        if (newPassword is null || newPassword.Length is < 12 or > 1024 || newPassword == currentPassword) throw new ArgumentException("Contraseña nueva inválida.");
        SetPassword(username, newPassword, false);
    }

    public void SetPassword(string username, string password, bool mustChange)
    {
        var salt = RandomNumberGenerator.GetBytes(16); using var db = Open();
        using var cmd = new NpgsqlCommand("UPDATE panel_users SET salt=@salt,password_hash=@hash,must_change=@change WHERE lower(username)=lower(@user)", db);
        cmd.Parameters.AddWithValue("user", username.Trim()); cmd.Parameters.AddWithValue("salt", salt); cmd.Parameters.AddWithValue("hash", Hash(password, salt)); cmd.Parameters.AddWithValue("change", mustChange);
        if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("Usuario inexistente.");
    }

    public void Assign(string username, string account, bool assigned)
    {
        using var db = Open();
        using var cmd = new NpgsqlCommand(assigned
            ? "INSERT INTO panel_assignments(username,account_id) SELECT u.username,r.id FROM panel_users u CROSS JOIN panel_accounts r WHERE lower(u.username)=lower(@user) AND u.role='asistente' AND r.id=@account AND r.active ON CONFLICT DO NOTHING"
            : "DELETE FROM panel_assignments WHERE lower(username)=lower(@user) AND account_id=@account", db);
        cmd.Parameters.AddWithValue("user", username.Trim()); cmd.Parameters.AddWithValue("account", account);
        if (cmd.ExecuteNonQuery() == 0 && assigned && !AssignedAccounts(username).Contains(account)) throw new ArgumentException("Usuario asistente o RUC inexistente.");
    }

    public PanelSettings Preferences(string username)
    {
        using var db = Open(); using var cmd = new NpgsqlCommand("SELECT default_account,download_subfolder FROM panel_preferences WHERE lower(username)=lower(@user)", db);
        cmd.Parameters.AddWithValue("user", username); using var r = cmd.ExecuteReader();
        return r.Read() ? new PanelSettings { DefaultAccount = r.IsDBNull(0) ? null : r.GetString(0), DownloadSubfolder = r.IsDBNull(1) ? null : r.GetString(1) } : new PanelSettings();
    }
    public void SavePreferences(string username, PanelSettings settings)
    {
        if (settings.DefaultAccount is not null && !AssignedAccounts(username).Contains(settings.DefaultAccount)) throw new ArgumentException("Cuenta no asignada.");
        if (!string.IsNullOrWhiteSpace(settings.DownloadSubfolder)) SriAutomation.Application.Validation.Identifier(settings.DownloadSubfolder);
        using var db = Open(); using var cmd = new NpgsqlCommand("INSERT INTO panel_preferences(username,default_account,download_subfolder) SELECT username,@account,@folder FROM panel_users WHERE lower(username)=lower(@user) ON CONFLICT(username) DO UPDATE SET default_account=excluded.default_account,download_subfolder=excluded.download_subfolder", db);
        cmd.Parameters.AddWithValue("user", username); cmd.Parameters.AddWithValue("account", NpgsqlTypes.NpgsqlDbType.Text, (object?)settings.DefaultAccount ?? DBNull.Value); cmd.Parameters.AddWithValue("folder", NpgsqlTypes.NpgsqlDbType.Text, (object?)settings.DownloadSubfolder ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("Usuario inexistente.");
    }
    public void SyncAccounts(IEnumerable<Account> accounts) { using var db = Open(); using var tx = db.BeginTransaction(); SyncAccounts(db, tx, accounts); tx.Commit(); }
    internal static void SyncAccounts(NpgsqlConnection db, NpgsqlTransaction tx, IEnumerable<Account> accounts)
    {
        using (var reset = new NpgsqlCommand("UPDATE panel_accounts SET active=false", db, tx)) reset.ExecuteNonQuery();
        foreach (var account in accounts)
        {
            using var cmd = new NpgsqlCommand("INSERT INTO panel_accounts(id,name,tax_id,active,is_test) VALUES(@id,@name,@tax,true,@test) ON CONFLICT(id) DO UPDATE SET name=excluded.name,tax_id=excluded.tax_id,active=true,is_test=excluded.is_test", db, tx);
            cmd.Parameters.AddWithValue("id", account.Id); cmd.Parameters.AddWithValue("name", account.Name); cmd.Parameters.AddWithValue("tax", NpgsqlTypes.NpgsqlDbType.Text, (object?)account.TaxId ?? DBNull.Value); cmd.Parameters.AddWithValue("test", account.IsTest); cmd.ExecuteNonQuery();
        }
    }
    public void SyncAudit(IEnumerable<AuditEvent> events) { using var db = Open(); using var tx = db.BeginTransaction(); SyncAudit(db, tx, events); tx.Commit(); }
    internal static void SyncAudit(NpgsqlConnection db, NpgsqlTransaction tx, IEnumerable<AuditEvent> events)
    {
        foreach (var e in events)
        {
            using var cmd = new NpgsqlCommand("INSERT INTO panel_audit(id,username,action,occurred_at,account_id,result,is_test,account_name) VALUES(@id,@user,@action,@at,@account,@result,@test,@name) ON CONFLICT(id) DO NOTHING", db, tx);
            cmd.Parameters.AddWithValue("id", e.Id); cmd.Parameters.AddWithValue("user", e.User); cmd.Parameters.AddWithValue("action", e.Action); cmd.Parameters.AddWithValue("at", e.At.ToUniversalTime()); cmd.Parameters.AddWithValue("account", NpgsqlTypes.NpgsqlDbType.Text, (object?)e.Account ?? DBNull.Value); cmd.Parameters.AddWithValue("result", e.Result); cmd.Parameters.AddWithValue("test", e.IsTest); cmd.Parameters.AddWithValue("name", NpgsqlTypes.NpgsqlDbType.Text, (object?)e.AccountName ?? DBNull.Value); cmd.ExecuteNonQuery();
        }
    }
}

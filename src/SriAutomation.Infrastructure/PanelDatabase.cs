using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class PanelDatabase : IPanelDatabase
{
    private readonly string connectionString;
    public PanelDatabase(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "panel.db"), DefaultTimeout = 30 }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS users (username TEXT PRIMARY KEY COLLATE NOCASE, role TEXT NOT NULL, salt BLOB, password_hash BLOB); CREATE TABLE IF NOT EXISTS audit (id TEXT PRIMARY KEY, username TEXT NOT NULL, action TEXT NOT NULL, at TEXT NOT NULL, account TEXT, result TEXT NOT NULL, is_test INTEGER NOT NULL, account_name TEXT);";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS ruc (id TEXT PRIMARY KEY, name TEXT NOT NULL, tax_id TEXT, active INTEGER NOT NULL DEFAULT 1); CREATE TABLE IF NOT EXISTS asignacion_ruc (username TEXT COLLATE NOCASE NOT NULL, ruc_id TEXT NOT NULL, PRIMARY KEY(username,ruc_id), FOREIGN KEY(username) REFERENCES users(username), FOREIGN KEY(ruc_id) REFERENCES ruc(id)); CREATE TABLE IF NOT EXISTS user_preferences (username TEXT PRIMARY KEY COLLATE NOCASE, default_account TEXT, download_subfolder TEXT);"; cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA table_info(users)"; using var columns = cmd.ExecuteReader(); var hasMustChange = false; while(columns.Read()) if(columns.GetString(1)=="must_change") hasMustChange=true; columns.Close();
        if(!hasMustChange) { try { cmd.CommandText="ALTER TABLE users ADD COLUMN must_change INTEGER NOT NULL DEFAULT 0"; cmd.ExecuteNonQuery(); } catch(SqliteException ex) when(ex.Message.Contains("duplicate column name")) { } }
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); using var pragma=db.CreateCommand(); pragma.CommandText="PRAGMA foreign_keys=ON"; pragma.ExecuteNonQuery(); return db; }
    public void Initialize(string bootstrapUser, string bootstrapPassword)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var (name, role) in new[] { ("Cristopher", "admin"), ("Ivonne", "admin"), ("Bayron", "asistente"), ("Alexis", "asistente") })
        { using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT OR IGNORE INTO users(username,role) VALUES($u,$r)"; cmd.Parameters.AddWithValue("$u", name); cmd.Parameters.AddWithValue("$r", role); cmd.ExecuteNonQuery(); }
        using (var cmd = db.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "INSERT OR IGNORE INTO users(username,role) VALUES($u,'admin')"; cmd.Parameters.AddWithValue("$u", bootstrapUser); cmd.ExecuteNonQuery(); }
        using (var cmd = db.CreateCommand()) { cmd.Transaction = tx; cmd.CommandText = "UPDATE users SET salt=$s,password_hash=$h WHERE username=$u AND role='admin' AND password_hash IS NULL"; var salt=RandomNumberGenerator.GetBytes(16); cmd.Parameters.AddWithValue("$s",salt); cmd.Parameters.AddWithValue("$h",Hash(bootstrapPassword,salt)); cmd.Parameters.AddWithValue("$u",bootstrapUser); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
    private static byte[] Hash(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
    public (string User, string Role, bool MustChange)? Authenticate(string username, string password)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT username,role,salt,password_hash,must_change FROM users WHERE username=$u"; cmd.Parameters.AddWithValue("$u",username.Trim()); using var reader=cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2) || reader.IsDBNull(3)) return null;
        var expected=(byte[])reader[3]; return CryptographicOperations.FixedTimeEquals(Hash(password,(byte[])reader[2]),expected) ? (reader.GetString(0),reader.GetString(1),reader.GetInt64(4)!=0) : null;
    }
    public object ListUsers()
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT username,role,password_hash IS NOT NULL FROM users ORDER BY username"; using var reader=cmd.ExecuteReader(); var users=new List<object>(); while(reader.Read()) users.Add(new { username=reader.GetString(0), role=reader.GetString(1), active=reader.GetInt64(2)!=0 }); return users;
    }
    public void SaveUser(string username, string role, string password)
    {
        username=username?.Trim() ?? ""; if(username.Length is < 2 or > 80 || username.Any(char.IsControl)) throw new ArgumentException("Usuario inválido.");
        if(role is not "admin" and not "asistente") throw new ArgumentException("Rol inválido.");
        if(password is null || password.Length is < 12 or > 1024) throw new ArgumentException("La contraseña debe tener entre 12 y 1024 caracteres.");
        var salt=RandomNumberGenerator.GetBytes(16); using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="INSERT INTO users(username,role,salt,password_hash,must_change) VALUES($u,$r,$s,$h,1) ON CONFLICT(username) DO UPDATE SET salt=excluded.salt,password_hash=excluded.password_hash,must_change=1 WHERE users.role=excluded.role";
        cmd.Parameters.AddWithValue("$u",username); cmd.Parameters.AddWithValue("$r",role); cmd.Parameters.AddWithValue("$s",salt); cmd.Parameters.AddWithValue("$h",Hash(password,salt));
        if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("El usuario ya existe con otro rol.");
    }
    public void ChangePassword(string username, string currentPassword, string newPassword)
    {
        if (Authenticate(username,currentPassword) is null) throw new ArgumentException("Contraseña actual incorrecta.");
        if (newPassword is null || newPassword.Length is < 12 or > 1024 || newPassword == currentPassword) throw new ArgumentException("Use una contraseña diferente de al menos 12 caracteres.");
        SetPassword(username,newPassword,false);
    }
    public void SetPassword(string username, string password, bool mustChange)
    {
        var salt=RandomNumberGenerator.GetBytes(16); using var db=Open(); using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE users SET salt=$s,password_hash=$h,must_change=$m WHERE username=$u";
        cmd.Parameters.AddWithValue("$s",salt); cmd.Parameters.AddWithValue("$h",Hash(password,salt)); cmd.Parameters.AddWithValue("$m",mustChange?1:0); cmd.Parameters.AddWithValue("$u",username);
        if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("Usuario inexistente.");
    }
    public void SyncAccounts(IEnumerable<Account> accounts)
    {
        using var db=Open(); using var tx=db.BeginTransaction();
        using(var reset=db.CreateCommand()) { reset.Transaction=tx; reset.CommandText="UPDATE ruc SET active=0"; reset.ExecuteNonQuery(); }
        foreach(var a in accounts) { using var cmd=db.CreateCommand(); cmd.Transaction=tx; cmd.CommandText="INSERT INTO ruc(id,name,tax_id,active) VALUES($id,$name,$tax,1) ON CONFLICT(id) DO UPDATE SET name=excluded.name,tax_id=excluded.tax_id,active=1"; cmd.Parameters.AddWithValue("$id",a.Id); cmd.Parameters.AddWithValue("$name",a.Name); cmd.Parameters.AddWithValue("$tax",(object?)a.TaxId ?? DBNull.Value); cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
    public HashSet<string> AssignedAccounts(string user)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT a.ruc_id FROM asignacion_ruc a JOIN ruc r ON r.id=a.ruc_id WHERE a.username=$u AND r.active=1"; cmd.Parameters.AddWithValue("$u",user); using var reader=cmd.ExecuteReader(); var result=new HashSet<string>(StringComparer.Ordinal); while(reader.Read()) result.Add(reader.GetString(0)); return result;
    }
    public void Assign(string username,string account,bool assigned)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText=assigned ? "INSERT OR IGNORE INTO asignacion_ruc(username,ruc_id) SELECT u.username,r.id FROM users u JOIN ruc r WHERE u.username=$u AND u.role='asistente' AND r.id=$r AND r.active=1" : "DELETE FROM asignacion_ruc WHERE username=$u AND ruc_id=$r"; cmd.Parameters.AddWithValue("$u",username); cmd.Parameters.AddWithValue("$r",account); if(cmd.ExecuteNonQuery()==0 && assigned && !AssignedAccounts(username).Contains(account)) throw new ArgumentException("Usuario asistente o RUC inexistente.");
    }
    public PanelSettings Preferences(string username)
    {
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT default_account,download_subfolder FROM user_preferences WHERE username=$u"; cmd.Parameters.AddWithValue("$u",username); using var reader=cmd.ExecuteReader(); return reader.Read()?new PanelSettings {DefaultAccount=reader.IsDBNull(0)?null:reader.GetString(0),DownloadSubfolder=reader.IsDBNull(1)?null:reader.GetString(1)}:new PanelSettings();
    }
    public void SavePreferences(string username,PanelSettings settings)
    {
        if(settings.DefaultAccount is not null && !AssignedAccounts(username).Contains(settings.DefaultAccount)) throw new ArgumentException("Cuenta no asignada.");
        if(!string.IsNullOrWhiteSpace(settings.DownloadSubfolder)) SriAutomation.Application.Validation.Identifier(settings.DownloadSubfolder);
        using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="INSERT INTO user_preferences(username,default_account,download_subfolder) VALUES($u,$a,$d) ON CONFLICT(username) DO UPDATE SET default_account=$a,download_subfolder=$d"; cmd.Parameters.AddWithValue("$u",username); cmd.Parameters.AddWithValue("$a",(object?)settings.DefaultAccount??DBNull.Value); cmd.Parameters.AddWithValue("$d",(object?)settings.DownloadSubfolder??DBNull.Value); cmd.ExecuteNonQuery();
    }
    public void SyncAudit(IEnumerable<AuditEvent> events)
    {
        using var db=Open(); using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="INSERT OR IGNORE INTO audit(id,username,action,at,account,result,is_test,account_name) VALUES($id,$user,$action,$at,$account,$result,$test,$name)";
        foreach(var e in events) { cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id",e.Id); cmd.Parameters.AddWithValue("$user",e.User); cmd.Parameters.AddWithValue("$action",e.Action); cmd.Parameters.AddWithValue("$at",e.At.ToString("O")); cmd.Parameters.AddWithValue("$account",(object?)e.Account ?? DBNull.Value); cmd.Parameters.AddWithValue("$result",e.Result); cmd.Parameters.AddWithValue("$test",e.IsTest?1:0); cmd.Parameters.AddWithValue("$name",(object?)e.AccountName ?? DBNull.Value); cmd.ExecuteNonQuery(); } tx.Commit();
    }
}

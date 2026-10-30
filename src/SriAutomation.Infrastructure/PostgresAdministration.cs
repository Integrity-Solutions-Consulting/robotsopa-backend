using Npgsql;
namespace SriAutomation.Infrastructure;
public static class PostgresAdministration
{
    public static void Initialize(string connectionString)
    {
        var password = Environment.GetEnvironmentVariable("SRI_ADMIN_PASSWORD");
        if (password is null) throw new InvalidOperationException("Falta la variable SRI_ADMIN_PASSWORD en el entorno de inicialización.");
        if (password.Length is < 16 or > 1024) throw new InvalidOperationException($"SRI_ADMIN_PASSWORD tiene una longitud inválida ({password.Length}); debe tener entre 16 y 1024 unidades UTF-16.");
        Migrate(connectionString);
        new PostgresPanelDatabase(connectionString).Initialize("Cristopher", password);
        Verify(connectionString);
    }
    public static void Migrate(string connectionString)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        foreach (var version in new[] { "002", "003" })
        {
            using var stream = typeof(PostgresAdministration).Assembly.GetManifestResourceStream($"SriAutomation.Migrations.{version}.sql") ?? throw new InvalidOperationException("Migración ausente.");
            using var reader = new StreamReader(stream); using var cmd = new NpgsqlCommand(reader.ReadToEnd(), db); cmd.ExecuteNonQuery();
        }
    }
    public static void Verify(string connectionString)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        using var cmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM sri_schema_migrations WHERE version IN (2,3))=2 AND EXISTS (SELECT 1 FROM app_state WHERE id=1) AND EXISTS (SELECT 1 FROM panel_users WHERE role='admin' AND password_hash IS NOT NULL)", db);
        if (cmd.ExecuteScalar() is not true) throw new InvalidOperationException("La base no está inicializada.");
        // Resolve every column used by the repositories, without modifying data.
        using var schema = new NpgsqlCommand("SELECT id,payload FROM app_state LIMIT 0; SELECT username,role,salt,password_hash,must_change FROM panel_users LIMIT 0; SELECT id,name,tax_id,active,is_test FROM panel_accounts LIMIT 0; SELECT username,account_id FROM panel_assignments LIMIT 0; SELECT username,default_account,download_subfolder FROM panel_preferences LIMIT 0; SELECT id,username,action,occurred_at,account_id,result,is_test,account_name FROM panel_audit LIMIT 0", db);
        using var reader = schema.ExecuteReader();
        while (reader.NextResult()) { }
    }
}

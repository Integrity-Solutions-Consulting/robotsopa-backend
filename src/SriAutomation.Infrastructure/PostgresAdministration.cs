using Npgsql;
namespace SriAutomation.Infrastructure;
public static class PostgresAdministration
{
    public static void Initialize(string connectionString)
    {
        var password = Environment.GetEnvironmentVariable("SRI_ADMIN_PASSWORD");
        if (password is null) throw new InvalidOperationException("Falta la variable SRI_ADMIN_PASSWORD en el entorno de inicialización.");
        if (password.Length is < 16 or > 1024) throw new InvalidOperationException($"SRI_ADMIN_PASSWORD tiene una longitud inválida ({password.Length}); debe tener entre 16 y 1024 unidades UTF-16.");
        var username = Environment.GetEnvironmentVariable("SRI_ADMIN_USERNAME");
        if (string.IsNullOrWhiteSpace(username)) throw new InvalidOperationException("Falta SRI_ADMIN_USERNAME en el entorno de inicialización.");
        Migrate(connectionString);
        new PostgresPanelDatabase(connectionString).Initialize(username, password);
        Verify(connectionString);
    }
    public static void Migrate(string connectionString)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        foreach (var version in new[] { "002", "003", "004", "005", "006" })
        {
            using var stream = typeof(PostgresAdministration).Assembly.GetManifestResourceStream($"SriAutomation.Migrations.{version}.sql") ?? throw new InvalidOperationException("Migración ausente.");
            using var reader = new StreamReader(stream); using var cmd = new NpgsqlCommand(reader.ReadToEnd(), db); cmd.ExecuteNonQuery();
        }
    }
    public static void Verify(string connectionString)
    {
        using var db = new NpgsqlConnection(connectionString); db.Open();
        foreach (var (table, columns) in RequiredColumns)
        {
            using var relation = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", db);
            relation.Parameters.AddWithValue("name", table);
            if (relation.ExecuteScalar() is not true) throw new InvalidOperationException($"Esquema PostgreSQL incompatible: falta la tabla {table}.");
            foreach (var (column, type) in columns)
            {
                using var check = new NpgsqlCommand("SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid=to_regclass(@table) AND attname=@column AND attnum>0 AND NOT attisdropped", db);
                check.Parameters.AddWithValue("table", table); check.Parameters.AddWithValue("column", column);
                var actual = check.ExecuteScalar() as string;
                if (actual is null) throw new InvalidOperationException($"Esquema PostgreSQL incompatible: falta la columna {table}.{column}.");
                if (actual != type && !(type == "text" && (actual == "character varying" || actual.StartsWith("character varying(", StringComparison.Ordinal))))
                    throw new InvalidOperationException($"Esquema PostgreSQL incompatible: {table}.{column} tiene tipo {actual}; se requiere {type}.");
            }
        }
        foreach (var (table, privilege) in RequiredPrivileges)
        {
            foreach (var operation in privilege.Split(", "))
            {
                using var check = new NpgsqlCommand("SELECT has_table_privilege(to_regclass(@table), @privilege)", db);
                check.Parameters.AddWithValue("table", table); check.Parameters.AddWithValue("privilege", operation);
                if (check.ExecuteScalar() is not true) throw new InvalidOperationException($"Permiso PostgreSQL ausente: {operation} en {table}.");
            }
        }
        foreach (var (table, keys) in RequiredUniqueKeys)
        {
            using var check = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_index i
                    WHERE i.indrelid=to_regclass(@table) AND i.indisunique AND i.indisvalid AND i.indimmediate
                      AND i.indpred IS NULL AND i.indnkeyatts=cardinality(@keys)
                      AND ARRAY(SELECT pg_get_indexdef(i.indexrelid, n, true) FROM generate_series(1, i.indnkeyatts) n ORDER BY 1)
                        = ARRAY(SELECT k FROM unnest(@keys) k ORDER BY k))
                """, db);
            check.Parameters.AddWithValue("table", table); check.Parameters.AddWithValue("keys", keys);
            if (check.ExecuteScalar() is not true) throw new InvalidOperationException($"Esquema PostgreSQL incompatible: falta un índice único válido en {table} ({string.Join(", ", keys)}).");
        }
        using var state = new NpgsqlCommand("SELECT payload::text FROM app_state WHERE id=1", db);
        if (state.ExecuteScalar() is not string payload) throw new InvalidOperationException("Falta la configuración app_state con id=1.");
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidOperationException("app_state.payload debe contener un objeto JSON.");
        }
        catch (System.Text.Json.JsonException ex) { throw new InvalidOperationException("app_state.payload no contiene JSON válido.", ex); }
        using var admin = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM panel_users WHERE role='admin' AND salt IS NOT NULL AND octet_length(salt)>0 AND password_hash IS NOT NULL AND octet_length(password_hash)=32)", db);
        if (admin.ExecuteScalar() is not true) throw new InvalidOperationException("Falta un administrador válido en panel_users (salt y password_hash requeridos).");
    }

    private static readonly (string Table, string Privilege)[] RequiredPrivileges =
    [
        ("app_state", "SELECT, UPDATE"), ("execution_ownership", "SELECT, INSERT, UPDATE"),
        ("panel_users", "SELECT, INSERT, UPDATE"), ("panel_accounts", "SELECT, INSERT, UPDATE"),
        ("panel_assignments", "SELECT, INSERT, DELETE"), ("panel_preferences", "SELECT, INSERT, UPDATE"),
        ("panel_audit", "SELECT, INSERT"), ("remote_deliveries", "SELECT, INSERT, UPDATE"),
        ("delivery_agents", "SELECT, INSERT, UPDATE")
    ];

    private static readonly (string Table, string[] Keys)[] RequiredUniqueKeys =
    [
        ("execution_ownership", ["job_id"]), ("panel_users", ["lower(username)"]),
        ("panel_preferences", ["username"]), ("panel_accounts", ["id"]),
        ("remote_deliveries", ["username", "document_id", "kind", "sha256"])
    ];

    private static readonly (string Table, (string Column, string Type)[] Columns)[] RequiredColumns =
    [
        ("app_state", [("id", "smallint"), ("payload", "jsonb")]),
        ("panel_users", [("username", "text"), ("role", "text"), ("salt", "bytea"), ("password_hash", "bytea"), ("must_change", "boolean")]),
        ("panel_accounts", [("id", "text"), ("name", "text"), ("tax_id", "text"), ("active", "boolean"), ("is_test", "boolean")]),
        ("panel_assignments", [("username", "text"), ("account_id", "text")]),
        ("panel_preferences", [("username", "text"), ("default_account", "text"), ("download_subfolder", "text")]),
        ("panel_audit", [("id", "text"), ("username", "text"), ("action", "text"), ("occurred_at", "timestamp with time zone"), ("account_id", "text"), ("result", "text"), ("is_test", "boolean"), ("account_name", "text")]),
        ("execution_ownership", [("job_id", "text"), ("worker_id", "text"), ("lease_id", "uuid"), ("fencing_token", "bigint"), ("lease_expires_at", "timestamp with time zone"), ("coordination_state", "text"), ("attempts", "integer"), ("acquired_at", "timestamp with time zone"), ("renewed_at", "timestamp with time zone"), ("updated_at", "timestamp with time zone")]),
        ("remote_deliveries", [("id", "uuid"), ("username", "text"), ("account_id", "text"), ("document_id", "text"), ("kind", "text"), ("source_path", "text"), ("destination_path", "text"), ("sha256", "text"), ("status", "text"), ("attempts", "integer"), ("last_error", "text"), ("created_at", "timestamp with time zone"), ("delivered_at", "timestamp with time zone")]),
        ("delivery_agents", [("id", "uuid"), ("username", "text"), ("label", "text"), ("secret_hash", "bytea"), ("created_at", "timestamp with time zone"), ("revoked_at", "timestamp with time zone"), ("last_seen_at", "timestamp with time zone")])
    ];
}

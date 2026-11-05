using Npgsql;
using SriAutomation.Infrastructure;
using SriAutomation.Domain;

namespace SriAutomation.Tests;

// Set only to a disposable test database, never to production. Each test creates its own schema.
public sealed class IsolatedPostgresFactAttribute : FactAttribute
{
    public IsolatedPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SRI_ISOLATED_TEST_POSTGRES")))
            Skip = "Se requiere SRI_ISOLATED_TEST_POSTGRES apuntando a una base de prueba desechable.";
    }
}

public class PostgresCompatibilityTests
{
    [IsolatedPostgresFact]
    public void CompatibleSchemaDoesNotRequireMigrationHistory()
    {
        using var fixture = new SchemaFixture();
        fixture.Execute("DROP TABLE sri_schema_migrations");
        PostgresAdministration.Verify(fixture.ConnectionString);
    }

    [IsolatedPostgresFact]
    public void MissingWorkerColumnIsReportedPrecisely()
    {
        using var fixture = new SchemaFixture();
        fixture.Execute("ALTER TABLE execution_ownership DROP COLUMN fencing_token");
        var error = Assert.Throws<InvalidOperationException>(() => PostgresAdministration.Verify(fixture.ConnectionString));
        Assert.Contains("execution_ownership.fencing_token", error.Message);
    }

    [IsolatedPostgresFact]
    public void ExistingPanelAccountSurvivesVerificationReadsAndWorkerUpdates()
    {
        using var fixture = new SchemaFixture();
        fixture.Execute("INSERT INTO panel_accounts(id,name,tax_id,active,is_test) VALUES ('existing','Original','1790000000001',true,false)");
        PostgresAdministration.Verify(fixture.ConnectionString);
        var repository = new PostgresStateRepository(fixture.ConnectionString);
        repository.Read(s => s.Accounts.Count);
        repository.Update(s => { s.WorkerHeartbeat = DateTimeOffset.UtcNow; return true; });
        Assert.Equal("Original|1790000000001|true", fixture.Scalar("SELECT name || '|' || tax_id || '|' || active::text FROM panel_accounts WHERE id='existing'"));
    }

    [IsolatedPostgresFact]
    public void ConflictingAccountIsNotOverwritten()
    {
        using var fixture = new SchemaFixture();
        fixture.Execute("INSERT INTO panel_accounts(id,name,tax_id,active,is_test) VALUES ('existing','Original','1790000000001',true,false)");
        var repository = new PostgresStateRepository(fixture.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => repository.Update(s => { s.Accounts.Add(new Account("existing", "Distinta", "1790000000002")); return true; }));
        Assert.Equal("Original|1790000000001|true", fixture.Scalar("SELECT name || '|' || tax_id || '|' || active::text FROM panel_accounts WHERE id='existing'"));
    }

    private sealed class SchemaFixture : IDisposable
    {
        private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
        private readonly string adminConnection = Environment.GetEnvironmentVariable("SRI_ISOLATED_TEST_POSTGRES")!;
        public string ConnectionString { get; }
        public SchemaFixture()
        {
            using var db = new NpgsqlConnection(adminConnection); db.Open();
            using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", db); create.ExecuteNonQuery();
            ConnectionString = new NpgsqlConnectionStringBuilder(adminConnection) { SearchPath = schema }.ConnectionString;
            PostgresAdministration.Migrate(ConnectionString);
            Execute("INSERT INTO panel_users(username,role,salt,password_hash) VALUES ('testadmin','admin',decode('01020304','hex'),decode(repeat('01',32),'hex'))");
        }
        public void Execute(string sql)
        {
            using var db = new NpgsqlConnection(ConnectionString); db.Open();
            using var command = new NpgsqlCommand(sql, db); command.ExecuteNonQuery();
        }
        public string? Scalar(string sql)
        {
            using var db = new NpgsqlConnection(ConnectionString); db.Open();
            using var command = new NpgsqlCommand(sql, db); return command.ExecuteScalar() as string;
        }
        public void Dispose()
        {
            using var db = new NpgsqlConnection(adminConnection); db.Open();
            using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db); drop.ExecuteNonQuery();
        }
    }
}

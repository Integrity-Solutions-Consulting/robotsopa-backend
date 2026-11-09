using Npgsql;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class PostgresZipJobStoreTests
{
    [Fact]
    public void RejectsInvalidPublicationBeforeConnecting()
    {
        var store = new PostgresZipJobStore("invalid");
        var lease = new ZipJobLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        Assert.Throws<ArgumentException>(() => store.Finish(lease, "Ready", "../archive.zip", new string('a', 64), 100, "[]", "[]"));
        Assert.Throws<ArgumentException>(() => store.Finish(lease, "Ready", lease.JobId.ToString("N") + ".zip", "bad", 100, "[]", "[]"));
        Assert.Throws<ArgumentException>(() => store.Finish(lease, "Failed", "archive.zip", null, null, "[]", "[]"));
        Assert.Throws<ArgumentException>(() => store.Progress(lease, 1, 2));
    }
    [IsolatedPostgresFact]
    public void PersistentJobsFenceExpiredOwnersAndRestrictUsers()
    {
        var baseConnection = Environment.GetEnvironmentVariable("SRI_ISOLATED_TEST_POSTGRES")!;
        var schema = "zip_" + Guid.NewGuid().ToString("N");
        using (var db = new NpgsqlConnection(baseConnection)) { db.Open(); using var cmd = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", db); cmd.ExecuteNonQuery(); }
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
        void Sql(string sql) { using var db = new NpgsqlConnection(connection); db.Open(); using var cmd = new NpgsqlCommand(sql, db); cmd.ExecuteNonQuery(); }
        try {
            foreach (var fixture in new[] { "002_state_foundation.sql", "003_panel.sql", "004_execution_ownership.sql", "005_remote_deliveries.sql", "006_delivery_agents.sql" })
                Sql(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/migrations", fixture))));
            Sql(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/cloudbeaver_007_document_zip_jobs.sql"))));
            Sql(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/cloudbeaver_007_document_zip_jobs.sql"))));
            PostgresAdministration.VerifyZipSchema(connection);
            Sql("INSERT INTO panel_users(username,role,salt,password_hash) VALUES ('owner','admin',decode('01020304','hex'),decode(repeat('01',32),'hex'))");
            var store = new PostgresZipJobStore(connection); var id = Guid.NewGuid();
            store.Create(id, "owner", "account", "{}", new string('a',64), 2, DateTimeOffset.UtcNow.AddMinutes(20));
            Assert.NotNull(new PostgresZipJobStore(connection).Find(id, "owner", "account"));
            Assert.Null(store.Find(id, "other", "account")); Assert.Null(store.Find(id, "owner", "other"));
            var first = store.Claim(id, Guid.NewGuid(), TimeSpan.FromMinutes(1))!;
            Assert.Null(store.Claim(id, Guid.NewGuid(), TimeSpan.FromMinutes(1)));
            Assert.True(store.Progress(first, 1, 0));
            Sql("UPDATE document_zip_jobs SET lease_expires_at=clock_timestamp()-interval '1 second'");
            Assert.Contains(id, store.Recoverable());
            var next = new PostgresZipJobStore(connection).Claim(id, Guid.NewGuid(), TimeSpan.FromMinutes(1))!;
            Assert.True(next.Generation > first.Generation);
            Assert.False(store.Progress(first, 2, 0));
            Assert.False(store.Finish(first, "Ready", id.ToString("N") + "-" + first.LeaseId.ToString("N") + ".zip", new string('a',64), 100, "[]", "[]"));
            Assert.True(store.Finish(next, "Ready", id.ToString("N") + "-" + next.LeaseId.ToString("N") + ".zip", new string('a',64), 100, "[]", "[]"));
            Assert.Equal("Ready", store.Find(id,"owner","account")!.Status);
            Sql("UPDATE document_zip_jobs SET expires_at=clock_timestamp()-interval '1 second'");
            Assert.Null(store.Find(id,"owner","account"));
        } finally { using var db = new NpgsqlConnection(baseConnection); db.Open(); using var cmd = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db); cmd.ExecuteNonQuery(); }
    }
}

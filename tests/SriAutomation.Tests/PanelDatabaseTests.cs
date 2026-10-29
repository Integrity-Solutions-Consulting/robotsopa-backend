using Microsoft.Data.Sqlite;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public class PanelDatabaseTests
{
    [Fact]
    public void UsersRequireIndividualPasswordsAndAuditSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sri-panel-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new PanelDatabase(directory); db.Initialize("Cristopher", "initial-admin-password");
            Assert.Equal("admin", db.Authenticate("cristopher", "initial-admin-password")?.Role);
            Assert.Null(db.Authenticate("Ivonne", "initial-admin-password"));
            db.SaveUser("Ivonne", "admin", "ivonne-password-123");
            db.SaveUser("Bayron", "asistente", "bayron-password-123");
            Assert.Null(db.Authenticate("Bayron", "ivonne-password-123"));
            Assert.Equal("asistente", db.Authenticate("bayron", "bayron-password-123")?.Role);
            Assert.True(db.Authenticate("bayron", "bayron-password-123")?.MustChange);
            db.SyncAccounts([new Account("one", "Uno", "1790000000001"), new Account("two", "Dos", "1790000000002")]);
            db.Assign("Bayron", "one", true); Assert.Contains("one", db.AssignedAccounts("Bayron"));
            Assert.DoesNotContain("two", db.AssignedAccounts("Bayron"));
            Assert.Throws<ArgumentException>(() => db.SavePreferences("Bayron", new PanelSettings { DefaultAccount = "two" }));
            db.ChangePassword("Bayron", "bayron-password-123", "bayron-personal-password");
            Assert.False(db.Authenticate("Bayron", "bayron-personal-password")?.MustChange);
            db.Assign("Bayron", "one", false); Assert.Empty(db.AssignedAccounts("Bayron"));
            Assert.Throws<ArgumentException>(() => db.SaveUser("Bayron", "admin", "another-password-123"));
            db.SyncAudit([new AuditEvent("event-1", "Bayron", "Prueba", DateTimeOffset.UtcNow, null, "OK")]);
            db = new PanelDatabase(directory); db.Initialize("Cristopher", "different-bootstrap-password");
            Assert.Null(db.Authenticate("Cristopher", "different-bootstrap-password"));
            Assert.NotNull(db.Authenticate("Cristopher", "initial-admin-password"));
            using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "panel.db")}"); connection.Open();
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM audit WHERE username='Bayron'";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

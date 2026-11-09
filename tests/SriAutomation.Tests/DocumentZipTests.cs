using System.Text.Json;
using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using Npgsql;

namespace SriAutomation.Tests;

public class DocumentZipTests
{
    private static JsonElement Json(object x) => JsonSerializer.SerializeToElement(x, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [IsolatedPostgresFact]
    public async Task PreparesScopesAndRejectsMissingCorruptAndOtherUsers()
    {
        var baseConnection = Environment.GetEnvironmentVariable("SRI_ISOLATED_TEST_POSTGRES")!;
        var schema = "zip_service_" + Guid.NewGuid().ToString("N");
        using (var db = new NpgsqlConnection(baseConnection)) { db.Open(); using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", db); create.ExecuteNonQuery(); }
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
        foreach (var fixture in new[] { "002_state_foundation.sql", "003_panel.sql", "004_execution_ownership.sql", "005_remote_deliveries.sql", "006_delivery_agents.sql" }) {
            using var db = new NpgsqlConnection(connection); db.Open();
            using var baseline = new NpgsqlCommand(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/migrations", fixture))), db); baseline.ExecuteNonQuery();
        }
        using (var db = new NpgsqlConnection(connection)) { db.Open(); using var schemaSql = new NpgsqlCommand(File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/cloudbeaver_007_document_zip_jobs.sql"))), db); schemaSql.ExecuteNonQuery(); }
        PostgresAdministration.VerifyZipSchema(connection);
        using (var db = new NpgsqlConnection(connection)) { db.Open(); using var seed = new NpgsqlCommand("INSERT INTO panel_users(username,role,salt,password_hash) VALUES ('owner','admin',decode('01020304','hex'),decode(repeat('01',32),'hex')); INSERT INTO panel_accounts(id,name,tax_id) VALUES ('a','A','1790000000001')", db); seed.ExecuteNonQuery(); }
        var root = Path.Combine(Path.GetTempPath(), "zip-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            var bytes = new byte[] { 1, 2, 3 }; var hash = DownloadPipeline.Hash(bytes);
            File.WriteAllBytes(Path.Combine(root, "file.pdf"), bytes); File.WriteAllBytes(Path.Combine(root, "file.xml"), bytes);
            var state = new State { Accounts = [new("a", "A", "1790000000001")], Documents = [new Voucher { Account = "a", Type = "Facturas", AccessKey = new string('1',49), Issued = new(2026,10,1), PdfPath = "file.pdf", XmlPath = "file.xml", PdfSha256 = hash, XmlSha256 = hash }] };
            using var zip = new DocumentZipService(new MemoryRepo(state), new DocumentStorage(root), new PostgresZipJobStore(connection));
            foreach (var scope in new[] { new DocumentZipService.Request(null,null,null), new(2026,null,null), new(2026,10,null), new(2026,10,"Facturas") }) {
                var created = Json(zip.Start("owner", "a", scope)); var id = created.GetProperty("id").GetString()!;
                JsonElement status = default;
                for (int n=0;n<100;n++) { status=Json(zip.Status("owner","a",id)); if (status.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
                Assert.Equal("Listo", status.GetProperty("status").GetString());
                Assert.Throws<KeyNotFoundException>(() => zip.Status("other", "a", id));
                using var restarted = new DocumentZipService(new MemoryRepo(state), new DocumentStorage(root), new PostgresZipJobStore(connection));
                Assert.Equal("Listo", Json(restarted.Status("owner", "a", id)).GetProperty("status").GetString());
            }
            File.WriteAllBytes(Path.Combine(root, "file.pdf"), [9]);
            state.Documents[0].PdfSha256 = new string('0',64);
            var bad = Json(zip.Start("owner", "a", new(2026,10,"Facturas"))); var badId = bad.GetProperty("id").GetString()!;
            JsonElement result = default;
            for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",badId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
            Assert.Equal("Incompleto",result.GetProperty("status").GetString());
            Assert.Contains("integridad incorrecta",result.GetProperty("issues").ToString());
            Assert.Throws<KeyNotFoundException>(() => zip.Download("other", "a", badId));
            state.Documents[0].PdfPath = null;
            state.Documents[0].XmlSha256 = null;
            var missing = Json(zip.Start("owner", "a", new(2026,10,"Facturas", "both"))); var missingId = missing.GetProperty("id").GetString()!;
            for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",missingId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
            Assert.Equal("Incompleto",result.GetProperty("status").GetString());
            Assert.Contains("no registrado", result.GetProperty("issues").ToString());
            Assert.Contains("sin hash verificable", result.GetProperty("issues").ToString());
            state.Documents[0].XmlSha256 = hash;
            foreach (var format in new[] { "xml", "auto" }) {
                var selected = Json(zip.Start("owner", "a", new(2026,10,"Facturas",format))); var selectedId = selected.GetProperty("id").GetString()!;
                for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",selectedId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
                Assert.Equal("Listo", result.GetProperty("status").GetString());
                if (format == "auto") Assert.Contains("formatos originales desconocidos", result.GetProperty("warnings").ToString());
            }
            var original = new Execution { Query = new("a", new(2026,10,1), new(2026,10,1), ["Facturas"], Pdf: false, Xml: true), AttemptedKeys = [new string('1',49)] };
            state.Executions.Add(original);
            var auto = Json(zip.Start("owner", "a", new(2026,10,"Facturas","auto"))); var autoId = auto.GetProperty("id").GetString()!;
            for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",autoId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
            Assert.Equal("Listo", result.GetProperty("status").GetString());
            Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
            state.Documents[0].PdfPath = "file.pdf"; state.Documents[0].PdfSha256 = DownloadPipeline.Hash([9]);
            state.Documents[0].XmlPath = null;
            original.Query = original.Query with { Pdf = true, Xml = false };
            foreach (var format in new[] { "pdf", "auto" }) {
                var selected = Json(zip.Start("owner", "a", new(2026,10,"Facturas",format))); var selectedId = selected.GetProperty("id").GetString()!;
                for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",selectedId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
                Assert.Equal("Listo", result.GetProperty("status").GetString());
            }
            state.Documents.Clear();
            var empty = Json(zip.Start("owner", "a", new(2026,10,"Facturas"))); var emptyId = empty.GetProperty("id").GetString()!;
            for (int n=0;n<100;n++) { result=Json(zip.Status("owner","a",emptyId)); if (result.GetProperty("status").GetString() != "Preparando") break; await Task.Delay(30); }
            Assert.Equal("Incompleto",result.GetProperty("status").GetString());
        } finally { Directory.Delete(root, true); using var db = new NpgsqlConnection(baseConnection); db.Open(); using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db); drop.ExecuteNonQuery(); }
    }
    private sealed class MemoryRepo(State state) : IStateRepository {
        public T Read<T>(Func<State,T> read) => read(state);
        public T Update<T>(Func<State,T> update) => update(state);
    }
}

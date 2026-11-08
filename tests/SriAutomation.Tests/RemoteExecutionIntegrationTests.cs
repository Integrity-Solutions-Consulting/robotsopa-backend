using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SriAutomation.Api;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;
using SriAutomation.Automation;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace SriAutomation.Tests;

public sealed class RemoteExecutionIntegrationTests
{
    [IsolatedPostgresFact]
    public async Task ClaimsRenewalRecoveryFilesAndCompletionUseSameQueue()
    {
        var schema = "remote_" + Guid.NewGuid().ToString("N");
        var baseConnection = Environment.GetEnvironmentVariable("SRI_ISOLATED_TEST_POSTGRES")!;
        var connection = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
        var directory = Path.Combine(Path.GetTempPath(), "sri-remote-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldPath = Environment.GetEnvironmentVariable("SRI_CREDENTIAL_PATH");
        var oldKey = Environment.GetEnvironmentVariable("SRI_CREDENTIAL_KEY_FILE");
        var oldBase64 = Environment.GetEnvironmentVariable("SRI_CREDENTIAL_KEY_BASE64");
        var vaultPath = Path.Combine(directory, "credentials"); var keyFile = Path.Combine(directory, "credential.key");
        File.WriteAllBytes(keyFile, RandomNumberGenerator.GetBytes(32));
        Environment.SetEnvironmentVariable("SRI_CREDENTIAL_PATH", vaultPath);
        Environment.SetEnvironmentVariable("SRI_CREDENTIAL_KEY_FILE", keyFile);
        Environment.SetEnvironmentVariable("SRI_CREDENTIAL_KEY_BASE64", null);
        void Sql(string text) { using var db = new NpgsqlConnection(connection); db.Open(); using var cmd = new NpgsqlCommand(text, db); cmd.ExecuteNonQuery(); }
        using (var db = new NpgsqlConnection(baseConnection)) { db.Open(); using var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", db); create.ExecuteNonQuery(); }
        try
        {
            var verifySql = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/postgres/verificar_004_006_solo_lectura.sql"));
            using (var emptyDb = new NpgsqlConnection(connection))
            {
                emptyDb.Open(); using var verifyEmpty = new NpgsqlCommand(await File.ReadAllTextAsync(verifySql), emptyDb); using var reader = await verifyEmpty.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync()); Assert.True(await reader.NextResultAsync());
                while (await reader.ReadAsync()) { Assert.False(reader.GetBoolean(3)); Assert.False(reader.GetBoolean(4)); Assert.False(reader.GetBoolean(6)); }
                Assert.True(await reader.NextResultAsync()); while (await reader.ReadAsync()) Assert.False(reader.GetBoolean(2));
            }
            PostgresAdministration.Migrate(connection);
            using (var db = new NpgsqlConnection(connection))
            {
                db.Open(); using var verify = new NpgsqlCommand(await File.ReadAllTextAsync(verifySql), db); using var reader = await verify.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(await reader.NextResultAsync());
                var columns = 0; while (await reader.ReadAsync()) { Assert.True(reader.GetBoolean(3)); Assert.True(reader.GetBoolean(4)); Assert.True(reader.GetBoolean(6)); columns++; } Assert.True(columns > 20);
                Assert.True(await reader.NextResultAsync());
                var indexes = 0; while (await reader.ReadAsync()) { Assert.True(reader.GetBoolean(2)); indexes++; } Assert.Equal(3, indexes);
            }
            Sql("INSERT INTO panel_users(username,role,salt,password_hash) VALUES ('tester','admin',decode('01020304','hex'),decode(repeat('01',32),'hex'))");
            var credentials = new DeliveryAgentCredentials(connection); var registered = credentials.Register("tester", "fixture");
            var registered2 = credentials.Register("tester", "fixture 2");
            var registered3 = credentials.Register("tester", "fixture 3");
            Assert.Equal("tester", credentials.Authenticate(registered.Id, registered.Secret));
            Assert.Null(credentials.Authenticate(registered.Id, new string('0', 64)));
            Sql("INSERT INTO panel_users(username,role,salt,password_hash) VALUES ('other','asistente',decode('01020304','hex'),decode(repeat('02',32),'hex'))");
            Assert.Empty(credentials.List("other"));
            Assert.False(credentials.Revoke(registered.Id, "other"));
            Assert.Null(credentials.Rotate(registered.Id, "other"));
            Assert.Equal("tester", credentials.Authenticate(registered.Id, registered.Secret));
            var rotated = credentials.Rotate(registered.Id, "tester");
            Assert.NotNull(rotated);
            Assert.Null(credentials.Authenticate(registered.Id, registered.Secret));
            Assert.Equal("tester", credentials.Authenticate(registered.Id, rotated));
            Assert.True(credentials.Revoke(registered.Id, "tester"));
            Assert.Null(credentials.Authenticate(registered.Id, rotated));
            Assert.Null(credentials.Rotate(registered.Id, "tester"));
            registered = credentials.Register("tester", "fixture after revocation");

            var repo = new PostgresStateRepository(connection);
            var date = new DateOnly(2026, 9, 12); const string taxId = "1790000000001"; const string password = "FICTICIA-unica-7$";
            const string key = "0109202601179000000000110010010000000011234567811";
            var logs = new CaptureLogs();
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Logging.AddProvider(logs); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IStorageProvider>(new DocumentStorage(directory)); builder.Services.AddSingleton<IDocumentParser, XmlDocumentParser>();
            builder.Services.AddSingleton<IStateRepository>(repo);
            await using var app = builder.Build();
            app.Use(async (c, next) => { try { await next(); } catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException) { c.Response.StatusCode = 400; } });
            app.Use(async (c, next) => { if (c.Request.Headers["X-Test-Https"] == "1") c.Request.Scheme = "https"; await next(); });
            app.MapPanelEndpoints(app.MapGroup("/api/v1"), new PostgresPanelDatabase(connection));
            app.MapGet("/api/v1/reports/{account}/{year:int}/{month:int}", MonthlyReportEndpoint.Download);
            RemoteExecutionEndpoints.Map(app, connection, c => Guid.TryParse(c.Request.Headers["X-Delivery-Agent-Id"], out var agent) ? credentials.Authenticate(agent, c.Request.Headers["X-Delivery-Agent-Secret"].ToString()) : null, credentials.CanAccess);
            var deliveries = new RemoteDeliveryStore(connection); var serverStorage = new DocumentStorage(directory);
            app.MapGet("/delivery-agent/v1/pending", () => deliveries.Pending("tester"));
            app.MapGet("/delivery-agent/v1/{id:guid}/file", (Guid id) => { var item = deliveries.GetFile("tester", id); return item is null ? Results.NotFound() : Results.File(serverStorage.Resolve(item.SourcePath), "application/octet-stream"); });
            app.MapPost("/delivery-agent/v1/{id:guid}/confirm", (Guid id, Confirmation input) => deliveries.Confirm("tester", id, input.Sha256) ? Results.NoContent() : Results.NotFound());
            app.MapPost("/delivery-agent/v1/{id:guid}/failure", (Guid id, Failure input) => deliveries.RecordFailure("tester", id, input.Code) ? Results.NoContent() : Results.NotFound());
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var panel = new HttpClient { BaseAddress = new Uri(address) };
            using var registration = await panel.PostAsJsonAsync("/api/v1/accounts/register", new { Alias = "Fixture", TaxId = taxId, Password = password });
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
            var registeredText = await registration.Content.ReadAsStringAsync(); Assert.DoesNotContain(password, registeredText);
            var account = System.Text.Json.JsonSerializer.Deserialize<Account>(registeredText, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.Id;
            Assert.DoesNotContain(password, await File.ReadAllTextAsync(Path.Combine(vaultPath, account + ".credential")));
            repo.Update(s => { s.Executions.Add(new() { Id = "job", RequestedBy = "tester", Query = new(account, date, date, ["Facturas"], Pdf: true, Xml: false) }); return true; });
            using var first = Client(address, registered.Id, registered.Secret); using var second = Client(address, registered2.Id, registered2.Secret);
            using var denied = await new HttpClient { BaseAddress = new Uri(address) }.PostAsync("/execution-agent/v1/claim", null);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            var claims = await Task.WhenAll(first.PostAsync("/execution-agent/v1/claim", null), second.PostAsync("/execution-agent/v1/claim", null));
            Assert.Single(claims, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(claims, r => r.StatusCode == HttpStatusCode.NoContent);
            var winner = claims[0].StatusCode == HttpStatusCode.OK ? first : second;
            var loser = winner == first ? second : first;
            var old = await claims.Single(r => r.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<Claim>();
            Headers(winner, old!);
            using (var unclaimed = await loser.PostAsync("/execution-agent/v1/job/credentials", null)) Assert.False(unclaimed.IsSuccessStatusCode);
            using (var renew = await winner.PostAsync("/execution-agent/v1/job/renew", null)) Assert.Equal(HttpStatusCode.OK, renew.StatusCode);
            Sql("UPDATE execution_ownership SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE job_id='job'");
            using var recovered = await loser.PostAsync("/execution-agent/v1/claim", null); Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var current = await recovered.Content.ReadFromJsonAsync<Claim>(); Assert.True(current!.Generation > old!.Generation); Headers(loser, current);
            using (var stale = await winner.PostAsync("/execution-agent/v1/job/renew", null)) Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using (var staleRead = await winner.GetAsync("/execution-agent/v1/job/state")) Assert.False(staleRead.IsSuccessStatusCode);
            using (var staleCredential = await winner.PostAsync("/execution-agent/v1/job/credentials", null)) Assert.False(staleCredential.IsSuccessStatusCode);
            var identity = loser == first ? registered : registered2;
            Assert.True(credentials.Revoke(identity.Id, "tester"));
            using (var revokedActive = await loser.PostAsync("/execution-agent/v1/job/credentials", null)) Assert.Equal(HttpStatusCode.Unauthorized, revokedActive.StatusCode);
            Sql("UPDATE execution_ownership SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE job_id='job'");
            using var third = Client(address, registered3.Id, registered3.Secret);
            using var thirdClaim = await third.PostAsync("/execution-agent/v1/claim", null); Assert.Equal(HttpStatusCode.OK, thirdClaim.StatusCode);
            var latest = (await thirdClaim.Content.ReadFromJsonAsync<Claim>())!;
            using var transport = Client(address, registered3.Id, registered3.Secret);
            var remote = new RemoteOwnedExecution(transport, new RemoteWorker.Claim(latest.JobId, latest.LeaseId, latest.Generation));
            Assert.True(await remote.Renew(CancellationToken.None));
            using (var insecure = Client(address, registered3.Id, registered3.Secret))
            {
                insecure.DefaultRequestHeaders.Remove("X-Test-Https"); Headers(insecure, latest);
                using var refused = await insecure.PostAsync("/execution-agent/v1/job/credentials", null);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }
            using (var fromBrowser = Client(address, registered3.Id, registered3.Secret))
            {
                Headers(fromBrowser, latest); fromBrowser.DefaultRequestHeaders.Add("Origin", "https://fixture.invalid");
                using var refused = await fromBrowser.PostAsync("/execution-agent/v1/job/credentials", null);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            }
            using (var secretRequest = Client(address, registered3.Id, registered3.Secret))
            {
                Headers(secretRequest, latest);
                using var authorized = await secretRequest.PostAsync("/execution-agent/v1/job/credentials", null);
                Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
                Assert.True(authorized.Headers.CacheControl?.NoStore);
            }
            using (var playwright = await Playwright.CreateAsync())
            await using (var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }))
            {
                var page = await browser.NewPageAsync();
                await page.RouteAsync("https://srienlinea.sri.gob.ec/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<form onsubmit=\"event.preventDefault();window.sent=[document.querySelector('#usuario').value,document.querySelector('#clave').value]\"><label for='usuario'>RUC</label><input id='usuario'><input id='clave' type='password'><button>Ingresar</button></form>" }));
                await page.GotoAsync("https://srienlinea.sri.gob.ec/auth/fixture");
                using var log = new StringWriter(); var oldOut = Console.Out; var oldError = Console.Error;
                try { Console.SetOut(log); Console.SetError(log); Assert.True(await remote.SubmitCredentials(page, taxId, CancellationToken.None)); }
                finally { Console.SetOut(oldOut); Console.SetError(oldError); }
                Assert.DoesNotContain(password, log.ToString());
                var submitted = await page.EvaluateAsync<string[]>("() => window.sent"); Assert.Equal(taxId, submitted[0]); Assert.Equal(password, submitted[1]);
                await page.Locator("#clave").FillAsync("");
                await page.EvaluateAsync("() => { const input = document.createElement('input'); input.autocomplete = 'one-time-code'; document.body.appendChild(input); }");
                Assert.False(await remote.SubmitCredentials(page, taxId, CancellationToken.None));
                Assert.Equal("", await page.Locator("#clave").InputValueAsync());
                var waiting = RemoteWorker.WaitForHuman(page, remote, "job", CancellationToken.None);
                await Task.Delay(300); await page.Locator("input[autocomplete=one-time-code]").EvaluateAsync("e => e.remove()");
                await waiting;
                Assert.Equal("WaitingForHuman", repo.Read(s => s.Executions.Single(e => e.Id == "job").Stage));
            }
            remote.Update(s => { s.Executions.Single().AttemptedKeys.Add(key); return true; });
            var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key]);
            var file = new DownloadedVoucher(account, "Facturas", key, date, null, pdf);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Pdf = [1, 2, 3] })).StatusCode);
            Assert.Contains("code=PDF_INVALID", logs.Text);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Issued = date.AddDays(1) })).StatusCode);
            Assert.Contains("code=JOB_DATE_OUT_OF_RANGE", logs.Text);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Account = "other" })).StatusCode);
            Assert.Contains("code=JOB_ACCOUNT_MISMATCH", logs.Text);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Type = "Retenciones" })).StatusCode);
            Assert.Contains("code=JOB_TYPE_MISMATCH", logs.Text);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Key = new string('8', 49) })).StatusCode);
            Assert.Contains("code=JOB_KEY_NOT_ATTEMPTED", logs.Text);
            var wrongIssuer = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "RUC: 9999999999999"]);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Pdf = wrongIssuer })).StatusCode);
            Assert.Contains("code=ISSUER_ACCOUNT_MISMATCH", logs.Text);
            var wrongDate = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "FECHA DE EMISION: 11/09/2026"]);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Pdf = wrongDate })).StatusCode);
            Assert.Contains("code=PDF_DATE_MISMATCH", logs.Text);
            var mismatchedKey = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + new string('9', 49)]);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Pdf = mismatchedKey })).StatusCode);
            Assert.Contains("code=PDF_KEY_MISMATCH", logs.Text);
            var uploaded = remote.Process(file); var duplicate = remote.Process(file); Assert.Equal(uploaded.Id, duplicate.Id);
            var differentPdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "EJEMPLAR DIFERENTE"]);
            Assert.Equal(HttpStatusCode.BadRequest, Assert.Throws<HttpRequestException>(() => remote.Process(file with { Pdf = differentPdf })).StatusCode);
            Assert.Contains("code=HISTORICAL_HASH_MISMATCH", logs.Text);
            Assert.Single(repo.Read(s => s.Documents));
            var redownloadRoot = Path.Combine(directory, "redownload");
            var redownloadLock = Path.Combine(directory, "redownload.lock");
            var localPdf = Path.Combine(redownloadRoot, taxId, "2026", "09", "Facturas", key + ".pdf");
            Assert.False(LocalDocumentAvailability.IsAvailable(redownloadRoot, taxId, uploaded, "pdf"));
            await DeliveryAgent.PollOnce(transport, redownloadRoot, redownloadLock, CancellationToken.None);
            Assert.True(LocalDocumentAvailability.IsAvailable(redownloadRoot, taxId, uploaded, "pdf"));
            File.Delete(localPdf);
            Assert.False(LocalDocumentAvailability.IsAvailable(redownloadRoot, taxId, uploaded, "pdf"));
            remote.Process(file);
            await DeliveryAgent.PollOnce(transport, redownloadRoot, redownloadLock, CancellationToken.None);
            Assert.True(LocalDocumentAvailability.IsAvailable(redownloadRoot, taxId, uploaded, "pdf"));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(localPdf)!, "*.pdf"));
            await using (var portal = await FixtureServer.Start(SriEngineFixtureTests.Html))
            using (var playwright = await Playwright.CreateAsync())
            await using (var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }))
            {
                var page = await browser.NewPageAsync(); await page.GotoAsync(portal.Url);
                var profile = SriEngineFixtureTests.Profile(); profile.Url = portal.Url; profile.Validate(true);
                await new SriExecutionEngine(remote, remote).Run(page, profile, remote.Read(s => s.Executions.Single()), taxId, CancellationToken.None);
                Assert.Equal(2, portal.Queries); Assert.True(portal.Files > 0);
            }
            Assert.Equal(2, repo.Read(s => s.Documents.Count));
            using (var finish = await remote.PostAsync("finish", CancellationToken.None)) Assert.Equal(HttpStatusCode.OK, finish.StatusCode);
            var final = repo.Read(s => s.Executions.Single(e => e.Id == "job"));
            Assert.Contains(final.Status, new[] { "Completed", "CompletedWithWarnings" }); Assert.Single(final.Reports);
            Assert.True(File.Exists(new DocumentStorage(directory).Resolve(final.Reports[0].Path)));
            var documents = repo.Read(s => s.Documents.ToList());
            Assert.All(documents, d => { Assert.NotNull(d.PdfPath); Assert.True(File.Exists(new DocumentStorage(directory).Resolve(d.PdfPath))); });
            Assert.Contains(final.Events, e => e.Stage == final.Status);
            Assert.Contains(repo.Read(s => s.Audit.ToList()), e => e.Action == "Resultado de procesamiento");
            using var reportResponse = await panel.GetAsync($"/api/v1/reports/{account}/2026/9");
            Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
            Assert.Equal("Resumen_2026_09.xlsx", reportResponse.Content.Headers.ContentDisposition?.FileNameStar);
            var reportBytes = await reportResponse.Content.ReadAsByteArrayAsync();
            var staleHash = new string('a', 64);
            deliveries.Enqueue("tester", account, $"report-{account}-2026-09", "xlsx", "unused.xlsx", $"{taxId}/2026/09/Reportes/Resumen_2026_09.xlsx", staleHash);
            Sql("UPDATE remote_deliveries SET created_at='2000-01-01T00:00:00Z' WHERE sha256='" + staleHash + "'");
            Assert.DoesNotContain(deliveries.Pending("tester"), d => d.Sha256 == staleHash);
            var desktop = Path.Combine(directory, "desktop"); var deliveryLock = Path.Combine(directory, "delivery.lock");
            await DeliveryAgent.PollOnce(transport, desktop, deliveryLock, CancellationToken.None);
            foreach (var document in documents)
                Assert.True(File.Exists(Path.Combine(desktop, taxId, "2026", "09", "Facturas", document.AccessKey + ".pdf")));
            var localReport = Path.Combine(desktop, taxId, "2026", "09", "Reportes", "Resumen_2026_09.xlsx");
            Assert.Equal(reportBytes, await File.ReadAllBytesAsync(localReport));
            await DeliveryAgent.PollOnce(transport, desktop, deliveryLock, CancellationToken.None);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(localReport)!, "*.xlsx"));
            var conflict = Path.Combine(desktop, taxId, "2026", "09", "Facturas", documents[0].AccessKey + ".pdf");
            await File.WriteAllTextAsync(conflict, "contenido distinto");
            Sql("UPDATE remote_deliveries SET status='Pending' WHERE document_id='" + documents[0].Id + "' AND kind='pdf'");
            await DeliveryAgent.PollOnce(transport, desktop, deliveryLock, CancellationToken.None);
            Assert.Equal(documents[0].PdfSha256, DownloadPipeline.Hash(await File.ReadAllBytesAsync(conflict)));
            Assert.DoesNotContain(deliveries.Pending("tester"), d => d.DocumentId == documents[0].Id && d.Kind == "pdf");
            var emptyDate = new DateOnly(2025, 1, 3);
            repo.Update(s => { s.Executions.Add(new() { Id = "empty", RequestedBy = "tester", Query = new(account, emptyDate, emptyDate, ["Facturas"], Pdf: true, Xml: false) }); return true; });
            using var emptyClaimResponse = await third.PostAsync("/execution-agent/v1/claim", null);
            var emptyClaim = await emptyClaimResponse.Content.ReadFromJsonAsync<Claim>(); Assert.NotNull(emptyClaim);
            var emptyRemote = new RemoteOwnedExecution(transport, new RemoteWorker.Claim(emptyClaim.JobId, emptyClaim.LeaseId, emptyClaim.Generation));
            emptyRemote.Update(s => { var e = s.Executions.Single(); e.Queries.Add(new(emptyDate, "Facturas", 0, 0, 0, "SinDocumentos")); e.SriCheckpoint = new(emptyDate, "Facturas", 0, 0, true); return true; });
            using (var emptyFinish = await emptyRemote.PostAsync("finish", CancellationToken.None)) Assert.Equal(HttpStatusCode.OK, emptyFinish.StatusCode);
            Assert.Single(repo.Read(s => s.Executions.Single(e => e.Id == "empty").Reports));
            await DeliveryAgent.PollOnce(transport, desktop, deliveryLock, CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(desktop, taxId, "2025", "01", "Reportes", "Resumen_2025_01.xlsx")));
            Assert.True(credentials.Revoke(registered3.Id, "tester")); Assert.Null(credentials.Authenticate(registered3.Id, registered3.Secret));
            using (var revoked = await third.PostAsync("/execution-agent/v1/claim", null)) Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            await app.StopAsync();
            Assert.DoesNotContain(password, logs.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SRI_CREDENTIAL_PATH", oldPath); Environment.SetEnvironmentVariable("SRI_CREDENTIAL_KEY_FILE", oldKey); Environment.SetEnvironmentVariable("SRI_CREDENTIAL_KEY_BASE64", oldBase64);
            using var db = new NpgsqlConnection(baseConnection); db.Open(); using var drop = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", db); drop.ExecuteNonQuery();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static HttpClient Client(string address, Guid id, string secret) { var client = new HttpClient { BaseAddress = new Uri(address) }; client.DefaultRequestHeaders.Add("X-Test-Https", "1"); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id.ToString()); return client; }
    private static void Headers(HttpClient client, Claim c) { client.DefaultRequestHeaders.Add("X-Execution-Lease", c.LeaseId.ToString()); client.DefaultRequestHeaders.Add("X-Execution-Generation", c.Generation.ToString()); }
    private sealed record Claim(string JobId, Guid LeaseId, long Generation);
    private sealed record Confirmation(string Sha256);
    private sealed record Failure(string Code);
    private sealed class CaptureLogs : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
        public string Text => string.Join('\n', messages);
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception) + exception);
    }
}

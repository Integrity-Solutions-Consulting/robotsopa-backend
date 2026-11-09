using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;
using SriAutomation.LocalAgent;
using Xunit.Abstractions;

namespace SriAutomation.Tests;

public sealed class CentralRealHttpTests(ITestOutputHelper output)
{
    [CentralHttpFact]
    public async Task LeaseExpiresNaturallyAndCheckpointSurvivesNewAgent()
    {
        using var panel = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri("http://127.0.0.1:59044") };
        using var login = await panel.PostAsJsonAsync("/api/v1/auth/login", new { username = "local_test_admin", password = "LocalTestOnly-2026!" }); login.EnsureSuccessStatusCode();
        panel.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        async Task<HttpClient> Agent(string label) {
            using var response = await panel.PostAsJsonAsync("/api/v1/delivery-agents", new { label }); response.EnsureSuccessStatusCode();
            var id = await response.Content.ReadFromJsonAsync<JsonElement>(); var client = new HttpClient { BaseAddress = panel.BaseAddress };
            client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id.GetProperty("id").GetString()); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", id.GetProperty("secret").GetString()); client.DefaultRequestHeaders.Add("X-SRI-Storage-Mode", "central-v1"); return client;
        }
        using var first = await Agent("lease fixture 1"); using var second = await Agent("lease fixture 2");
        var account = "lease" + Guid.NewGuid().ToString("N")[..12]; var date = new DateOnly(2026, 10, 12);
        (await panel.PostAsJsonAsync("/api/v1/accounts", new Account(account, "FICTICIA LEASE", "1790000000001"))).EnsureSuccessStatusCode();
        (await panel.PostAsJsonAsync("/api/v1/executions", new Query(account, date, date, ["Facturas"], "Sri"))).EnsureSuccessStatusCode();
        using var initial = await first.PostAsync("/execution-agent/v1/claim", null); initial.EnsureSuccessStatusCode(); var claim = (await initial.Content.ReadFromJsonAsync<RemoteWorker.Claim>())!;
        var old = new RemoteOwnedExecution(first, claim); old.Update(s => { s.Executions.Single().SriCheckpoint = new(date, "Facturas", 3, 7, false); return true; });
        Assert.Equal(HttpStatusCode.NoContent, (await second.PostAsync("/execution-agent/v1/claim", null)).StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(93));
        using var nextResponse = await second.PostAsync("/execution-agent/v1/claim", null); nextResponse.EnsureSuccessStatusCode();
        var next = (await nextResponse.Content.ReadFromJsonAsync<RemoteWorker.Claim>())!; Assert.Equal(claim.JobId, next.JobId); Assert.True(next.Generation > claim.Generation);
        Assert.False(await old.Renew(CancellationToken.None));
        var current = new RemoteOwnedExecution(second, next); var checkpoint = current.Read(s => s.Executions.Single().SriCheckpoint);
        Assert.Equal(3, checkpoint!.Page); Assert.Equal(7, checkpoint.Row);
        using var stop = await current.PostAsync("stop", CancellationToken.None); stop.EnsureSuccessStatusCode();
        output.WriteLine($"natural lease: generation {claim.Generation} -> {next.Generation}; checkpoint page=3 row=7; stale renewal rejected");
    }

    [CentralHttpFact]
    public async Task SyntheticTransferVolumeOverRealHttp()
    {
        var root = Path.Combine(Path.GetTempPath(), "sopa-central-volume-" + Guid.NewGuid().ToString("N"));
        using var panel = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri("http://127.0.0.1:59044") };
        using var login = await panel.PostAsJsonAsync("/api/v1/auth/login", new { username = "local_test_admin", password = "LocalTestOnly-2026!" });
        login.EnsureSuccessStatusCode(); panel.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var reg = await panel.PostAsJsonAsync("/api/v1/delivery-agents", new { label = "synthetic-volume" }); reg.EnsureSuccessStatusCode();
        var identity = await reg.Content.ReadFromJsonAsync<JsonElement>();
        var handler = new DropOnceHandler(new HttpClientHandler());
        using var transport = new HttpClient(handler) { BaseAddress = panel.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
        transport.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", identity.GetProperty("id").GetString());
        transport.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", identity.GetProperty("secret").GetString());
        transport.DefaultRequestHeaders.Add("X-SRI-Storage-Mode", "central-v1");
        var date = new DateOnly(2026, 10, 12);
        foreach (var count in new[] { 10, 50, 100, 250 })
        {
            var account = "perf" + Guid.NewGuid().ToString("N")[..12];
            (await panel.PostAsJsonAsync("/api/v1/accounts", new Account(account, "FICTICIA PERF", "1790000000001"))).EnsureSuccessStatusCode();
            (await panel.PostAsJsonAsync("/api/v1/executions", new Query(account, date, date, ["Facturas"], "Sri"))).EnsureSuccessStatusCode();
            using var response = await transport.PostAsync("/execution-agent/v1/claim", null); response.EnsureSuccessStatusCode();
            var claim = (await response.Content.ReadFromJsonAsync<RemoteWorker.Claim>())!;
            var queue = new PendingTransferQueue(Path.Combine(root, count.ToString()));
            var remote = new RemoteOwnedExecution(transport, claim, transferQueue: queue);
            var keys = Enumerable.Range(1, count).Select(i => i.ToString("D49")).ToArray();
            remote.Update(s => { s.Executions.Single().AttemptedKeys.AddRange(keys); return true; });
            var requests = handler.Count; var uploads = handler.Uploads; var watch = Stopwatch.StartNew();
            for (var i = 0; i < keys.Length; i++) { if (i % 20 == 0) Assert.True(await remote.Renew(CancellationToken.None)); var key = keys[i]; var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key]); var doc = remote.Process(new(account, "Facturas", key, date, null, pdf)); Assert.Equal(DownloadPipeline.Hash(pdf), doc.PdfSha256); }
            watch.Stop(); Assert.Empty(queue.Entries());
            using var stop = await remote.PostAsync("stop", CancellationToken.None); stop.EnsureSuccessStatusCode();
            output.WriteLine($"documents={count} http={handler.Count - requests} uploads={handler.Uploads - uploads} ms={watch.ElapsedMilliseconds} retries=0 duplicateUploads=0 pending=0 errors=0");
        }
    }

    [CentralHttpFact]
    public async Task SyntheticBotTransfersAgainstRealApiAndPostgres()
    {
        var root = Path.Combine(Path.GetTempPath(), "sopa-central-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var account = "central" + Guid.NewGuid().ToString("N")[..12];
        const string taxId = "1790000000001";
        var date = new DateOnly(2026, 10, 12);
        var key = "0109202601179000000000110010010000000011234567811";
        using var panel = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri("http://127.0.0.1:59044") };
        // The local isolated proxy declares HTTPS; no credentials or traffic leave loopback.
        using var login = await panel.PostAsJsonAsync("/api/v1/auth/login", new { username = "local_test_admin", password = "LocalTestOnly-2026!" });
        login.EnsureSuccessStatusCode();
        panel.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        using var createdAccount = await panel.PostAsJsonAsync("/api/v1/accounts", new Account(account, "FICTICIA CENTRAL", taxId));
        createdAccount.EnsureSuccessStatusCode();
        using var registration = await panel.PostAsJsonAsync("/api/v1/delivery-agents", new { label = "central synthetic" });
        registration.EnsureSuccessStatusCode();
        var agent = (await registration.Content.ReadFromJsonAsync<JsonElement>());
        var agentId = agent.GetProperty("id").GetString()!; var secret = agent.GetProperty("secret").GetString()!;
        using var anonymous = new HttpClient { BaseAddress = panel.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/execution-agent/v1/capabilities")).StatusCode);
        var handler = new DropOnceHandler(new HttpClientHandler());
        using var transport = new HttpClient(handler) { BaseAddress = panel.BaseAddress };
        transport.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", agentId);
        transport.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret);
        transport.DefaultRequestHeaders.Add("X-SRI-Storage-Mode", "central-v1");
        Assert.Equal("v1", (await transport.GetFromJsonAsync<JsonElement>("/execution-agent/v1/capabilities")).GetProperty("centralStorage").GetString());
        using var createJob = await panel.PostAsJsonAsync("/api/v1/executions", new Query(account, date, date, ["Facturas"], "Sri", true, true));
        createJob.EnsureSuccessStatusCode();
        var job = (await createJob.Content.ReadFromJsonAsync<Execution>())!;
        using var claimed = await transport.PostAsync("/execution-agent/v1/claim", null); claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<RemoteWorker.Claim>())!;
        Assert.Equal(job.Id, claim.JobId); Assert.Equal("central-v1", claim.StorageMode);
        var queue = new PendingTransferQueue(root);
        var remote = new RemoteOwnedExecution(transport, claim, deliveryRoot: Path.Combine(root, "must-not-create"), transferQueue: queue, supportsConditionalPending: true);
        Assert.True(remote.Read(s => s.Executions.Single().CentralStorageAgent));
        remote.Update(s => { s.Executions.Single().AttemptedKeys.Add(key); return true; });
        var pdf = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "SINTETICO " + account]);
        var xml = Encoding.UTF8.GetBytes($"<factura><infoTributaria><ruc>{taxId}</ruc><claveAcceso>{key}</claveAcceso><estab>001</estab><ptoEmi>001</ptoEmi><secuencial>000000001</secuencial></infoTributaria><infoFactura><fechaEmision>12/10/2026</fechaEmision><importeTotal>11.50</importeTotal></infoFactura></factura>");
        handler.FailBefore = true;
        Assert.Throws<HttpRequestException>(() => remote.Process(new(account, "Facturas", key, date, null, pdf)));
        Assert.Single(queue.Entries());
        queue = new PendingTransferQueue(root);
        remote = new RemoteOwnedExecution(transport, claim, deliveryRoot: Path.Combine(root, "must-not-create"), transferQueue: queue, supportsConditionalPending: true);
        remote.ReconcilePending(); Assert.Empty(queue.Entries());
        var stored = remote.Read(s => Assert.Single(s.Documents));
        Assert.True(remote.IsAvailable(stored, "pdf"));
        var same = remote.Process(new(account, "Facturas", key, date, null, pdf)); Assert.Equal(stored.Id, same.Id);
        handler.Drop = true;
        Assert.Throws<HttpRequestException>(() => remote.Process(new(account, "Facturas", key, date, xml, null)));
        Assert.Single(queue.Entries());
        var full = remote.Read(s => Assert.Single(s.Documents));
        Assert.True(remote.IsAvailable(full, "xml")); Assert.Empty(queue.Entries());
        Assert.Equal(DownloadPipeline.Hash(pdf), full.PdfSha256); Assert.Equal(DownloadPipeline.Hash(xml), full.XmlSha256);
        Assert.True(remote.IsAvailable(full, "xml")); Assert.Empty(queue.Entries()); Assert.False(Directory.Exists(Path.Combine(root, "must-not-create")));
        var serverPdf = Path.Combine("C:\\Users\\CRIS\\Desktop\\BOTSRI\\test-artifacts\\sopa-zip-local-output", full.PdfPath!);
        File.WriteAllBytes(serverPdf, [1, 2, 3]);
        Assert.False(remote.IsAvailable(full, "pdf"));
        full = remote.Process(new(account, "Facturas", key, date, null, pdf));
        Assert.True(remote.IsAvailable(full, "pdf"));
        File.Delete(serverPdf);
        Assert.False(remote.IsAvailable(full, "pdf"));
        full = remote.Process(new(account, "Facturas", key, date, null, pdf));
        Assert.True(remote.IsAvailable(full, "pdf"));
        var delivery = await transport.GetFromJsonAsync<JsonElement>("/delivery-agent/v1/pending");
        Assert.DoesNotContain(delivery.EnumerateArray(), x => x.GetProperty("documentId").GetString() == full.Id);
        foreach (var ext in new[] { "pdf", "xml" })
        {
            var bytes = await panel.GetByteArrayAsync($"/api/v1/documents/{full.Id}/{ext}");
            Assert.Equal(ext == "pdf" ? DownloadPipeline.Hash(pdf) : DownloadPipeline.Hash(xml), DownloadPipeline.Hash(bytes));
        }
        remote.Update(s => { var e = s.Executions.Single(); e.SriCheckpoint = new(date, "Facturas", 1, 1, true); e.Queries.Add(new(date, "Facturas", 1, 1, 0, "Consultado")); return true; });
        using var finish = await remote.PostAsync("finish", CancellationToken.None); finish.EnsureSuccessStatusCode();
        var finished = (await finish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString();
        Assert.Contains(finished, new[] { "Completed", "CompletedWithWarnings" });
        queue.Save(account, "Facturas", key, date, "pdf", pdf, job.Id, claim.Generation);
        queue = new PendingTransferQueue(root); // Reopen the durable queue as a new agent instance would.
        await Assert.ThrowsAsync<HttpRequestException>(() => RemoteWorker.ReconcileCompletedPending(anonymous, queue, CancellationToken.None));
        Assert.Single(queue.Entries());
        await RemoteWorker.ReconcileCompletedPending(transport, queue, CancellationToken.None);
        Assert.Empty(queue.Entries());
        var different = PdfFixture.BuildLines(["CLAVE DE ACCESO: " + key, "OTRA VERSION"]);
        queue.Save(account, "Facturas", key, date, "pdf", different, job.Id, claim.Generation);
        var beforeConfirmation = handler.Uploads;
        await RemoteWorker.ReconcileCompletedPending(transport, queue, CancellationToken.None);
        Assert.Single(queue.Entries()); Assert.Equal(beforeConfirmation, handler.Uploads);
        queue.Confirm(account, "Facturas", key, date, "pdf", DownloadPipeline.Hash(different));
        queue.Save(account, "Facturas", key, date, "pdf", pdf, job.Id, claim.Generation + 1);
        await RemoteWorker.ReconcileCompletedPending(transport, queue, CancellationToken.None);
        Assert.Single(queue.Entries());
        queue.Confirm(account, "Facturas", key, date, "pdf", DownloadPipeline.Hash(pdf));
        var folder = await panel.GetFromJsonAsync<JsonElement>($"/api/v1/document-explorer/{account}");
        Assert.Contains(folder.GetProperty("folders").EnumerateArray(), x => x.GetProperty("year").GetInt32() == 2026 && x.GetProperty("month").GetInt32() == 10);
        using var zipStart = await panel.PostAsJsonAsync($"/api/v1/document-explorer/{account}/zip", new { year = 2026, month = 10, type = "Facturas", formats = "both" });
        zipStart.EnsureSuccessStatusCode(); var zip = await zipStart.Content.ReadFromJsonAsync<JsonElement>();
        var zipId = zip.GetProperty("id").GetString();
        for (var i = 0; i < 100 && zip.GetProperty("status").GetString() == "Preparando"; i++) { await Task.Delay(100); zip = await panel.GetFromJsonAsync<JsonElement>($"/api/v1/document-explorer/{account}/zip/{zipId}"); }
        Assert.Equal("Listo", zip.GetProperty("status").GetString());
        using var archive = new ZipArchive(new MemoryStream(await panel.GetByteArrayAsync($"/api/v1/document-explorer/{account}/zip/{zipId}/file")), ZipArchiveMode.Read);
        Assert.Equal(2, archive.Entries.Count);
        foreach (var entry in archive.Entries) { using var stream = entry.Open(); using var hash = SHA256.Create(); Assert.Equal(entry.Name.EndsWith(".pdf") ? DownloadPipeline.Hash(pdf) : DownloadPipeline.Hash(xml), Convert.ToHexStringLower(hash.ComputeHash(stream))); }
        transport.DefaultRequestHeaders.Remove("X-SRI-Storage-Mode");
        using var legacyJob = await panel.PostAsJsonAsync("/api/v1/executions", new Query(account, date, date, ["Facturas"], "Sri")); legacyJob.EnsureSuccessStatusCode();
        using var legacyResponse = await transport.PostAsync("/execution-agent/v1/claim", null); legacyResponse.EnsureSuccessStatusCode();
        var legacy = (await legacyResponse.Content.ReadFromJsonAsync<RemoteWorker.Claim>())!; Assert.Equal("legacy", legacy.StorageMode);
        using var legacyStop = await new RemoteOwnedExecution(transport, legacy).PostAsync("stop", CancellationToken.None); legacyStop.EnsureSuccessStatusCode();
        output.WriteLine($"central synthetic job={job.Id} document={full.Id} HTTP={handler.Count} uploads={handler.Uploads} droppedResponses=1 queue=0");
    }

    private sealed class DropOnceHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Drop; public bool FailBefore; public int Count; public int Uploads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Count); bool files = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/files");
            if (files) Interlocked.Increment(ref Uploads);
            if (files && FailBefore) { FailBefore = false; throw new HttpRequestException("Desconexión sintética antes del envío."); }
            var response = await base.SendAsync(request, token);
            if (files && Drop) { Drop = false; response.Dispose(); throw new HttpRequestException("Respuesta sintética perdida tras almacenar."); }
            return response;
        }
    }
}

public sealed class CentralHttpFactAttribute : FactAttribute
{
    public CentralHttpFactAttribute() { if (Environment.GetEnvironmentVariable("SOPA_CENTRAL_HTTP") != "1") Skip = "Requiere API central local aislada explícita."; }
}

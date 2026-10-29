using System.Text;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Tests;

public sealed class SriEngineFixtureTests
{
    private const string Html = """
        <!doctype html><html><body><div id="authenticated">Sesión fixture</div><span id="account">1790000000001</span><div id="login" hidden>Login fixture</div><div id="challenge" hidden>Verificación fixture</div>
        <input id="date"><select id="type"><option value="01">Factura</option></select><button id="search" onclick="current=0;load()">Consultar</button>
        <div id="busy" hidden>Cargando</div><div id="ready"><table><tbody></tbody></table><div id="empty" hidden>Sin documentos</div></div><button id="next" onclick="current++;load()">Siguiente</button>
        <script>let current=0;async function load(){document.querySelector('#busy').hidden=false;const response=await fetch('/query',{method:'POST',body:JSON.stringify({page:current,date:document.querySelector('#date').value})});const data=await response.json();document.querySelector('tbody').innerHTML=data.keys.map(key=>`<tr><td class="key">${key}</td><td class="issued">12/09/2026</td><td><a class="xml" href="/files/${key}.xml" download>XML</a><a class="pdf" href="/files/${key}.pdf" download>PDF</a></td></tr>`).join('');document.querySelector('#empty').hidden=data.keys.length>0;document.querySelector('#next').disabled=!data.next;document.querySelector('#busy').hidden=true;}</script></body></html>
        """;
    private static PortalProfile Profile() => new()
    {
        Url = "https://fixture.invalid/",
        Verified = true,
        VerifiedAt = "2026-10-27",
        Evidence = "Fixture local, no selectores SRI.",
        AccountIdentity = "#account",
        Authenticated = "#authenticated",
        Login = "#login",
        HumanChallenge = "#challenge",
        Date = "#date",
        DateFormat = "dd/MM/yyyy",
        Type = "#type",
        TypeValues = new() { ["Facturas"] = "01" },
        Search = "#search",
        Ready = "#ready",
        Busy = "#busy",
        ResponseUrlPattern = @"/query$",
        Rows = "tbody tr",
        Empty = "#empty",
        Next = "#next",
        NextDisabled = "#next:disabled",
        Key = ".key",
        Issued = ".issued",
        Xml = ".xml",
        Pdf = ".pdf",
        MinQueryIntervalMs = 0,
        MaxDocuments = 10,
        MaxAutomaticDays = 3
    };
    [BrowserFact]
    public async Task JsfPaginationDownloadsAllPdfsAndRecoveryUsesPersistentManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new JsonStateRepository(Path.Combine(root, "state")); var storage = new DocumentStorage(Path.Combine(root, "out"));
            var query = new Query("a", new(2026, 9, 12), new(2026, 9, 12), ["Facturas"]);
            var job = repository.Update(s => { s.Accounts.Add(new("a", "Fixture", "1790000000001", true)); return Validation.Enqueue(s, query); });
            await using var server = await FixtureServer.Start(Html);
            using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await page.GotoAsync(server.Url);
            var profile = Profile(); profile.Url = server.Url; profile.Validate(true); var engine = new SriExecutionEngine(repository, new DownloadPipeline(repository, storage, new XmlDocumentParser()));
            profile.MaxDocuments = 1;
            await engine.Run(page, profile, job, "1790000000001", CancellationToken.None);
            Assert.Equal(2, server.Queries); Assert.Equal(2, server.Files); Assert.Equal(2, repository.Read(s => s.Documents.Count));
            Assert.True(repository.Read(s => s.Executions.Single().SriCheckpoint!.Complete));
            Assert.Equal(2, repository.Read(s => s.Executions.Single().Queries.Single().Found));
            Assert.All(repository.Read(s => s.Documents), d => { Assert.Null(d.XmlSha256); Assert.NotNull(d.PdfSha256); });
            var recovered = repository.Read(s => s.Executions.Single());
            await engine.Run(page, profile, recovered, "1790000000001", CancellationToken.None);
            Assert.Equal(2, server.Queries); Assert.Equal(2, server.Files);
        }
        finally { Directory.Delete(root, true); }
    }
    [BrowserFact]
    public async Task ExpiredWrongAccountAndChallengeSuspendBeforeQuery()
    {
        using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(); await page.SetContentAsync(Html);
        var manager = new SriSessionManager(Profile());
        await manager.Ensure(page, "1790000000001");
        await Assert.ThrowsAsync<HumanActionRequiredException>(() => manager.Ensure(page, "9999999999001"));
        await page.Locator("#login").EvaluateAsync("e=>e.hidden=false");
        await Assert.ThrowsAsync<HumanActionRequiredException>(() => manager.Ensure(page, "1790000000001"));
        await page.Locator("#login").EvaluateAsync("e=>e.hidden=true"); await page.Locator("#challenge").EvaluateAsync("e=>e.hidden=false");
        await Assert.ThrowsAsync<HumanActionRequiredException>(() => manager.Ensure(page, "1790000000001"));
    }
    [BrowserFact]
    public async Task EmptyDatesAreRecordedWithoutFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "sri-empty-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var repository = new JsonStateRepository(root); var query = new Query("a", new(2026, 9, 12), new(2026, 9, 14), ["Facturas"], BroadRangeApproved: true);
            var job = repository.Update(s => { s.Accounts.Add(new("a", "Fixture", "1790000000001", true)); return Validation.Enqueue(s, query); });
            using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
            await using var server = await FixtureServer.Start(Html, empty: true);
            await page.GotoAsync(server.Url);
            var engine = new SriExecutionEngine(repository, new RejectingPipeline());
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.Run(page, Profile(), job, "1790000000001", stop.Token));
            await engine.Run(page, Profile(), job, "1790000000001", CancellationToken.None);
            Assert.Equal(3, repository.Read(s => s.Executions.Single().Queries.Count)); Assert.All(repository.Read(s => s.Executions.Single().Queries), q => Assert.Equal("SinDocumentos", q.Status));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class RejectingPipeline : IDownloadPipeline { public Voucher Process(DownloadedVoucher download) => throw new Exception("No debe descargarse nada."); }
}
public sealed class BrowserTheoryAttribute : TheoryAttribute
{ public BrowserTheoryAttribute() { if (Environment.GetEnvironmentVariable("SRI_TEST_BROWSER") != "1") Skip = "Requiere SRI_TEST_BROWSER=1 y Chromium instalado."; } }

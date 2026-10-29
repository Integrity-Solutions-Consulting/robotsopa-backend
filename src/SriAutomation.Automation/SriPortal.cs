using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Automation;

// Los selectores reales deben obtenerse de una sesión autorizada, nunca inferirse.
public sealed class SriPortal : ISriComprobanteService
{
    public const string PortalUrl = "https://srienlinea.sri.gob.ec/comprobantes-electronicos-internet/pages/consultas/menu.jsf";
    private readonly IStateRepository? repository;
    private readonly IDownloadPipeline? downloads;
    public SriPortal() { }
    public SriPortal(IStateRepository repository, IDownloadPipeline downloads) { this.repository = repository; this.downloads = downloads; }
    public Task Execute(Query query, CancellationToken cancellation) => throw new HumanActionRequiredException("Se requiere una ejecución persistente, un perfil verificado y una sesión autorizada por cuenta.");
    public async Task Execute(SriAutomation.Domain.Execution execution, CancellationToken cancellation)
    {
        if (repository is null || downloads is null) throw new HumanActionRequiredException("Motor SRI sin servicios de persistencia configurados.");
        var profilePath = Environment.GetEnvironmentVariable("SRI_PORTAL_PROFILE") ?? "/app/config/sri-portal.json";
        var profile = File.Exists(profilePath)?PortalProfile.Load(profilePath):new PortalProfile();
        var account = repository.Read(s => s.Accounts.Single(a => a.Id == execution.Query.Account));
        if (account.IsTest) throw new HumanActionRequiredException("Una cuenta de pruebas no puede consultar el portal SRI real.");
        var taxId = account.TaxId ?? account.Id;
        if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new HumanActionRequiredException("Configure el RUC del emisor antes de abrir una sesión SRI.");
        var vault = Vault(); var session = await new WorkerAuthentication(repository).Connect(account,cancellation,execution.Id);
        try {
        var access=repository.Read(s=>s.Authentications.Last(a=>a.ExecutionId==execution.Id));
        if(access.Owner=="Agent"&&access.BrowserOpened) {
            repository.Update(s=>{var op=s.Authentications.Single(a=>a.Id==access.Id);op.Command="Run";ExecutionProgress.Set(s,execution.Id,"PreparingQuery","Acceso confirmado. El bot abre la consulta dentro de la sesión autorizada.");return true;});
            while(true) {
                await Task.Delay(1000,cancellation);
                var state=repository.Read(s=>s.Authentications.Single(a=>a.Id==access.Id));
                if(state.Status=="QueryCompleted")return;
                if(state.Status=="QueryFailed")throw new HumanActionRequiredException(state.Message);
                if(state.Status=="Cancelled")throw new OperationCanceledException();
                if(!repository.Read(AuthenticationFlow.AgentOnline))throw new HumanActionRequiredException("El agente Windows se desconectó durante la consulta. No se declaró éxito.");
            }
        }
        var configured=profile.Verified;
        if(configured)profile.Validate();
        Stage("PreparingQuery","Acceso confirmado. Abriendo la consulta en segundo plano; puede minimizar Angular.");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true, StorageState = session.StorageState });
        if(session.SessionStorage is not null)await context.AddInitScriptAsync("if(location.origin==='https://srienlinea.sri.gob.ec'){for(const [k,v] of Object.entries(JSON.parse("+System.Text.Json.JsonSerializer.Serialize(session.SessionStorage)+"))){sessionStorage.setItem(k,v)}}");
        context.SetDefaultTimeout(profile.TimeoutMs);
        var page = await context.NewPageAsync();
        using var registration = cancellation.Register(() => { _ = context.CloseAsync(); });
        try
        {
            await page.GotoAsync(configured?profile.Url:PortalUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded,Timeout=45000 });
            if(!configured){Stage("RecognizingControls","Reconociendo los filtros de comprobantes del SRI.");profile=await ObservedPortalProfile.Discover(page,taxId);profile.Validate();}
            await new SriExecutionEngine(repository, downloads).Run(page, profile, execution, taxId, cancellation);

        }
        catch (PlaywrightException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
        catch (PortalRecognitionException ex) { throw new HumanActionRequiredException(AccessMessages.For(ex.Code)??AccessMessages.For("QueryControlsMissing")!); }
        catch (TimeoutException) { throw new SriUnavailableException("El SRI no respondió dentro del tiempo configurado. No se confirmó el paso pendiente."); }
        catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_", StringComparison.Ordinal)) { throw new SriUnavailableException("El SRI no está accesible desde el Worker."); }
        catch (PlaywrightException) { throw new HumanActionRequiredException("No se pudo completar una interacción del portal. Revise los selectores en una sesión local autorizada."); }
        } finally { vault.Revoke(account.Id);repository.Update(s=>{var a=s.Authentications.LastOrDefault(a=>a.ExecutionId==execution.Id);if(a is not null){a.SessionVerified=false;a.Expires=null;if(a.BrowserOpened&&a.Status!="QueryFailed"){a.CancelRequested=true;a.Command="Cancel";}}return true;}); }
        void Stage(string stage,string message)=>repository.Update(s=>{ExecutionProgress.Set(s,execution.Id,stage,message);return true;});
    }
    public static SessionVault Vault() => new(Environment.GetEnvironmentVariable("SRI_SESSION_PATH") ?? Path.GetFullPath("storage/sessions"), Environment.GetEnvironmentVariable("SRI_SESSION_KEY_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SRI-Automation","secrets","session.key"));
    public static async Task Logout(string account, string taxId)
    {
        var vault = Vault();
        try
        {
            var profile = PortalProfile.Load(Environment.GetEnvironmentVariable("SRI_PORTAL_PROFILE") ?? Path.GetFullPath("config/sri-portal.json")); profile.Validate();
            var session = vault.Load(account, taxId);
            using var playwright = await Playwright.CreateAsync(); await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var context = await browser.NewContextAsync(new() { StorageState = session.StorageState }); var page = await context.NewPageAsync();
            await page.GotoAsync(profile.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var manager = new SriSessionManager(profile); await manager.Ensure(page, taxId); await manager.Logout(page);
            Console.WriteLine(string.IsNullOrWhiteSpace(profile.Logout) ? "Sesión local eliminada. Cierre remoto no confirmado: falta selector de salida verificado." : "Cierre de sesión solicitado en el portal y sesión local eliminada.");
        }
        finally { vault.Revoke(account); }
    }

    public static async Task Inspect(string output, CancellationToken cancellation)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = false });
        var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(PortalUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // Inspector local: el usuario autentica y examina los controles legítimamente.
        await page.PauseAsync();
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        // No persistir cookies, HTML autenticado, credenciales ni capturas con datos personales.
    }
}

public sealed class ConfiguredPage(IPage page, IReadOnlyDictionary<string, string> selectors)
{
    public ILocator Control(string name) => selectors.TryGetValue(name, out var selector) && !string.IsNullOrWhiteSpace(selector)
        ? page.Locator(selector) : throw new HumanActionRequiredException($"Falta selector validado: {name}.");
    public async Task Download(string control, string destination)
    {
        var download = await page.RunAndWaitForDownloadAsync(() => Control(control).ClickAsync());
        if (await download.FailureAsync() is not null) throw new IOException("Descarga incompleta.");
        await download.SaveAsAsync(destination);
    }
}

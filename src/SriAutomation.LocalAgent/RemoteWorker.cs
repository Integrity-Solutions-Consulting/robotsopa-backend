using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;

namespace SriAutomation.LocalAgent;

internal static class RemoteWorker
{
    public static async Task RunAsync(CancellationToken cancellation = default)
    {
        var address = Environment.GetEnvironmentVariable("SRI_AGENT_API") ?? "https://botisc.dokploy.integritysolutions.com.ec/";
        var id = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_ID"); var secret = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !Guid.TryParse(id, out _) || secret is null || secret.Length != 64 || !secret.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Configure la API HTTPS y la identidad del agente.");
        Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET", null);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation");
        Directory.CreateDirectory(directory);
        using var singleton = new FileStream(Path.Combine(directory, "remote-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        using var deliveryStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SRI-Automation");
        var delivery = Task.Run(async () => {
            while (!deliveryStop.IsCancellationRequested)
            {
                try { await DeliveryAgent.PollOnce(client, desktop, DeliveryAgent.LockPath(), deliveryStop.Token); }
                catch (OperationCanceledException) when (deliveryStop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
                try { await Task.Delay(TimeSpan.FromSeconds(10), deliveryStop.Token); }
                catch (OperationCanceledException) when (deliveryStop.IsCancellationRequested) { break; }
            }
        });
        var delay = 2;
        Console.WriteLine("Agente iniciado: consultando trabajos. Ctrl+C para detener.");
        try
        {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var response = await client.PostAsync("/execution-agent/v1/claim", null, shutdown.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                { Console.Error.WriteLine($"API respondiÃ³ HTTP {(int)response.StatusCode}; revise identidad y configuraciÃ³n del backend."); return; }
                response.EnsureSuccessStatusCode();
                delay = 2;
                if (response.StatusCode != HttpStatusCode.NoContent)
                {
                    var claim = await response.Content.ReadFromJsonAsync<Claim>(cancellationToken: shutdown.Token) ?? throw new InvalidDataException();
                    await Execute(client, claim, shutdown.Token);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine("ConexiÃ³n interrumpida; reintentando sin exponer credenciales.");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
                delay = Math.Min(delay * 2, 60);
            }
        }
        }
        finally { deliveryStop.Cancel(); await delivery; }
    }
    private static async Task Execute(HttpClient client, Claim claim, CancellationToken stopping)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var remote = new RemoteOwnedExecution(client, claim);
        var renewal = Task.Run(async () => {
            try { while (!work.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(10), work.Token); if (!await remote.Renew(work.Token)) { work.Cancel(); return; } } }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            catch (Exception) { work.Cancel(); }
        });
        try
        {
            var job = remote.Read(s => s.Executions.Single());
            var account = remote.Read(s => s.Accounts.Single()); var taxId = account.TaxId ?? account.Id;
            if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new HumanActionRequiredException("RUC invÃ¡lido.");
            Console.WriteLine("Trabajo recibido. Iniciando acceso al portal oficial del SRI.");
            for (var sessionAttempt = 0; ; sessionAttempt++)
            {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = false });
            var context = await browser.NewContextAsync(new() { AcceptDownloads = true });
            using var close = work.Token.Register(() => { _ = context.CloseAsync(); });
            var page = await context.NewPageAsync();
            remote.Update(s => { ExecutionProgress.Set(s, job.Id, "AuthenticatingApplication", "Iniciando sesiÃ³n en el portal oficial del SRI."); return true; });
            if (!await InteractiveNavigation.TryOpen(page, SriAccessEvidence.LoginUrl)) throw new HumanActionRequiredException("No se pudo abrir el portal.");
            BoundSriSession? bound = null;
            var submitted = false; var opened = false;
            for (var i = 0; i < 600; i++)
            {
                work.Token.ThrowIfCancellationRequested();
                foreach (var candidate in context.Pages.Where(p => !p.IsClosed).ToArray())
                {
                    if (await SriAccessEvidence.HumanChallenge(candidate)) await WaitForHuman(candidate, remote, job.Id, work.Token);
                    if ((await SriAccessEvidence.Inspect(candidate, taxId)).Verified) { page = candidate; bound = await BoundSriSession.Verify(page, taxId); break; }
                    if (!opened && await InteractiveNavigation.IsOfficialDocument(candidate))
                    {
                        var entry = candidate.GetByRole(AriaRole.Link, new() { Name = "Ir a iniciar sesiÃ³n", Exact = true });
                        if (await entry.CountAsync() == 1 && await entry.IsVisibleAsync()) { opened = true; await entry.ClickAsync(); }
                    }
                    if (!submitted && await candidate.Locator("input[type=password]:visible").CountAsync() == 1)
                    {
                        if (!await remote.SubmitCredentials(candidate, taxId, work.Token)) throw new HumanActionRequiredException("No se reconociÃ³ un formulario autorizado.");
                        submitted = true;
                    }
                }
                if (bound is not null) break;
                await Task.Delay(1000, work.Token);
            }
            if (bound is null) throw new HumanActionRequiredException("No se confirmÃ³ la identidad SRI.");
            remote.Update(s => { ExecutionProgress.Set(s, job.Id, "OpeningQuery", "Abriendo la consulta SRI autorizada."); return true; });
            page = await AuthorizedQueryNavigation.Open(page, bound, taxId, work.Token, authenticate: p => remote.SubmitCredentials(p, taxId, work.Token), humanAction: p => WaitForHuman(p, remote, job.Id, work.Token));
            var profile = await ObservedPortalProfile.Discover(page, taxId, session: bound); profile.Validate();
            try
            {
                for (var challengeAttempt = 0; ; challengeAttempt++)
                {
                    try { await new SriExecutionEngine(remote, remote).Run(page, profile, job, taxId, work.Token); break; }
                    catch (PortalRecognitionException ex) when (ex.Code == "HumanChallenge" && challengeAttempt == 0)
                    { await WaitForHuman(page, remote, job.Id, work.Token); job = remote.Read(s => s.Executions.Single()); }
                }
            }
            catch (PortalRecognitionException ex) when (ex.Code == "LoginRequired" && sessionAttempt == 0 && !work.IsCancellationRequested)
            {
                job = remote.Read(s => s.Executions.Single());
                Console.WriteLine("SesiÃ³n expirada; recuperando la consulta desde el Ãºltimo punto confirmado.");
                continue;
            }
            break;
            }
            using var finish = await remote.PostAsync("finish", work.Token); finish.EnsureSuccessStatusCode();
            var result = await finish.Content.ReadFromJsonAsync<FinishResult>(cancellationToken: work.Token);
            Console.WriteLine(result?.Status is "Completed" or "CompletedWithWarnings" ? "Trabajo finalizado; consulte los resultados en el panel." : "El backend terminÃ³ el procesamiento sin confirmar Ã©xito; revise el historial.");
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { Console.Error.WriteLine("EjecuciÃ³n detenida por cancelaciÃ³n o pÃ©rdida de conexiÃ³n."); }
        catch (HumanActionRequiredException ex) when (ex.Message == "HumanChallenge")
        {
            Console.Error.WriteLine("El SRI solicita CAPTCHA o MFA; revise el historial y reanude con intervenciÃ³n autorizada.");
            try { using var result = await remote.StopForHumanChallenge(); } catch (Exception) { }
        }
        catch (PortalRecognitionException ex) when (ex.Code == "HumanChallenge")
        {
            Console.Error.WriteLine("El SRI solicita CAPTCHA o MFA; revise el historial y reanude con intervenciÃ³n autorizada.");
            try { using var result = await remote.StopForHumanChallenge(); } catch (Exception) { }
        }
        catch (Exception ex) when (ex is HumanActionRequiredException or PortalRecognitionException or PlaywrightException or TimeoutException or IOException or HttpRequestException or InvalidOperationException)
        {
            Console.Error.WriteLine("EjecuciÃ³n detenida; revise el historial y la ventana SRI.");
            try { using var result = await remote.PostAsync("stop", CancellationToken.None); } catch (Exception) { }
        }
        catch (Exception)
        {
            Console.Error.WriteLine("EjecuciÃ³n interrumpida; revise el historial.");
            try { using var result = await remote.PostAsync("stop", CancellationToken.None); } catch (Exception) { }
        }
        finally { work.Cancel(); await renewal; }
    }
    internal static async Task WaitForHuman(IPage page, RemoteOwnedExecution remote, string jobId, CancellationToken token)
    {
        remote.Update(s => { ExecutionProgress.Set(s, jobId, "WaitingForHuman", "El SRI solicita verificaciÃ³n humana."); return true; });
        Console.WriteLine("El SRI solicita CAPTCHA o MFA. Complete la verificaciÃ³n en la ventana oficial.");
        await page.BringToFrontAsync();
        for (var i = 0; i < 600; i++)
        {
            token.ThrowIfCancellationRequested();
            if (page.IsClosed) throw new HumanActionRequiredException("HumanChallenge");
            if (!await SriAccessEvidence.HumanChallenge(page)) return;
            await Task.Delay(1000, token);
        }
        throw new HumanActionRequiredException("HumanChallenge");
    }
    internal sealed record Claim(string JobId, Guid LeaseId, long Generation);
    private sealed record FinishResult(string Status);
}

internal sealed class RemoteOwnedExecution(HttpClient client, RemoteWorker.Claim claim, Action<string>? trace = null) : IStateRepository, IDownloadPipeline
{
    private string Path => "/execution-agent/v1/" + Validation.Identifier(claim.JobId);
    private HttpRequestMessage Request(HttpMethod method, string suffix)
    {
        var request = new HttpRequestMessage(method, Path + suffix);
        request.Headers.Add("X-Execution-Lease", claim.LeaseId.ToString()); request.Headers.Add("X-Execution-Generation", claim.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }
    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token = default) { using (request) return await client.SendAsync(request, token); }
    public async Task<bool> Renew(CancellationToken token)
    {
        using var response = await Send(Request(HttpMethod.Post, "/renew"), token);
        if (!response.IsSuccessStatusCode) return false;
        var value = await response.Content.ReadFromJsonAsync<Renewal>(cancellationToken: token);
        return value is { Cancelled: false };
    }
    public async Task<bool> SubmitCredentials(IPage page, string expectedTaxId, CancellationToken token)
    {
        if (!await InteractiveNavigation.IsOfficialDocument(page) || !new Uri(page.Url).AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase) || await SriAccessEvidence.HumanChallenge(page)) return false;
        using var response = await Send(Request(HttpMethod.Post, "/credentials"), token);
        response.EnsureSuccessStatusCode();
        var credential = await response.Content.ReadFromJsonAsync<RemoteCredential>(cancellationToken: token) ?? throw new IOException("Credencial no disponible.");
        if (credential.Username != expectedTaxId || string.IsNullOrEmpty(credential.Password)) throw new InvalidDataException("Identidad de credencial incompatible.");
        token.ThrowIfCancellationRequested();
        return await AuthorizedLogin.Submit(page, credential.Username, credential.Password);
    }
    public Task<HttpResponseMessage> PostAsync(string operation, CancellationToken token) => Send(Request(HttpMethod.Post, "/" + operation), token);
    public Task<HttpResponseMessage> StopForHumanChallenge()
    {
        var request = Request(HttpMethod.Post, "/stop"); request.Headers.Add("X-Stop-Reason", "HumanChallenge");
        return Send(request);
    }
    public T Read<T>(Func<State, T> read)
    {
        using var response = Send(Request(HttpMethod.Get, "/state")).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        return read(response.Content.ReadFromJsonAsync<State>().GetAwaiter().GetResult() ?? throw new IOException("Estado ausente."));
    }
    public T Update<T>(Func<State, T> mutate)
    {
        using var response = Send(Request(HttpMethod.Get, "/state")).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        var state = response.Content.ReadFromJsonAsync<State>().GetAwaiter().GetResult() ?? throw new IOException("Estado ausente.");
        var result = mutate(state); var request = Request(HttpMethod.Put, "/state"); request.Content = JsonContent.Create(state);
        using var update = Send(request).GetAwaiter().GetResult(); update.EnsureSuccessStatusCode(); return result;
    }
    public bool IsAvailable(Voucher document, string extension)
    {
        using var response = Send(Request(HttpMethod.Get, "/available/" + Validation.Identifier(document.Id) + "/" + extension)).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        return response.Content.ReadFromJsonAsync<bool>().GetAwaiter().GetResult();
    }
    public Voucher Process(DownloadedVoucher file)
    {
        trace?.Invoke("ARCHIVO_ENVIADO_API");
        var request = Request(HttpMethod.Post, "/files"); request.Content = JsonContent.Create(file);
        using var response = Send(request).GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        var document = response.Content.ReadFromJsonAsync<Voucher>().GetAwaiter().GetResult() ?? throw new IOException("Documento no confirmado.");
        trace?.Invoke("ARCHIVO_CONFIRMADO_API");
        return document;
    }
    private sealed record Renewal(bool Cancelled);
    private sealed record RemoteCredential(string Username, string Password);
}


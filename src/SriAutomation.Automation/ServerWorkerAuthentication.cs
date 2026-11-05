using Microsoft.Playwright;
using System.Diagnostics;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Automation;

// Authentication runs in the worker; challenges are reported without bypassing them.
public sealed class ServerWorkerAuthentication(IStateRepository repository, SessionVault? scopedVault = null)
{
    public async Task<SessionData> Connect(Account account, CancellationToken cancellation, string? executionId = null)
    {
        if (account.IsTest) throw new HumanActionRequiredException("Las cuentas de prueba no pueden acceder al SRI real.");
        var taxId = account.TaxId ?? account.Id;
        var vault = scopedVault ?? SriPortal.Vault();
        SessionData? saved = null;
        try { saved = vault.Load(account.Id, taxId); Log(executionId, "session_loaded"); } catch (HumanActionRequiredException) { Log(executionId, "session_unavailable"); }
        var op = repository.Update(s => {
            var operation = new AuthenticationOperation { Account = account.Id, ExecutionId = executionId, Owner = "Worker", Status = "OpeningBrowser", Message = "El Worker está comprobando el acceso al SRI." };
            s.Authentications.Add(operation);
            if (executionId is not null) ExecutionProgress.Set(s, executionId, "OpeningBrowser", operation.Message);
            return operation;
        });
        var phase = "playwright_init";
        try
        {
            Log(executionId, phase);
            using var playwright = await Playwright.CreateAsync();
            phase = "chromium_launch";
            Log(executionId, phase);
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Timeout = 30000 });
            Log(executionId, "chromium_started");
            phase = "browser_context";
            var context = await browser.NewContextAsync(new() { StorageState = saved?.StorageState });
            if (saved?.SessionStorage is not null) await context.AddInitScriptAsync("if(location.origin==='https://srienlinea.sri.gob.ec'){for(const [k,v] of Object.entries(JSON.parse(" + System.Text.Json.JsonSerializer.Serialize(saved.SessionStorage) + "))){sessionStorage.setItem(k,v)}}");
            var page = await context.NewPageAsync();
            using var registration = cancellation.Register(() => { _ = context.CloseAsync(); });
            phase = "sri_navigation";
            var navigationClock = Stopwatch.StartNew();
            var resourceFailures = 0;
            var mainResponseStatus = 0;
            var mainFailure = "unknown";
            EventHandler<IRequest> onRequest = (_, request) => {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                    LogNavigation(executionId, request.RedirectedFrom is null ? "navigation_request" : "navigation_redirect", navigationClock.ElapsedMilliseconds, SafeDestination(request.Url));
            };
            EventHandler<IResponse> onResponse = (_, response) => {
                if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame) {
                    mainResponseStatus = response.Status;
                    LogNavigation(executionId, "navigation_response", navigationClock.ElapsedMilliseconds, SafeDestination(response.Url), response.Status);
                } else if (response.Status >= 400 && Interlocked.Increment(ref resourceFailures) <= 20)
                    LogNavigation(executionId, "resource_response_error", navigationClock.ElapsedMilliseconds, SafeDestination(response.Url), response.Status, resourceType: SafeResourceType(response.Request.ResourceType));
            };
            EventHandler<IRequest> onFailed = (_, request) => {
                var failure = ClassifyNetworkFailure(request.Failure);
                if (request.IsNavigationRequest && request.Frame == page.MainFrame) {
                    mainFailure = failure;
                    LogNavigation(executionId, "navigation_request_failed", navigationClock.ElapsedMilliseconds, SafeDestination(request.Url), failure: failure);
                } else if (Interlocked.Increment(ref resourceFailures) <= 20)
                    LogNavigation(executionId, "resource_request_failed", navigationClock.ElapsedMilliseconds, SafeDestination(request.Url), failure: failure, resourceType: SafeResourceType(request.ResourceType));
            };
            EventHandler<IPage> onDomContentLoaded = (_, _) =>
                LogNavigation(executionId, "dom_content_loaded", navigationClock.ElapsedMilliseconds, SafeDestination(page.Url));
            page.Request += onRequest;
            page.Response += onResponse;
            page.RequestFailed += onFailed;
            page.DOMContentLoaded += onDomContentLoaded;
            Log(executionId, phase);
            try {
                await OpenDocument(page, SriAccessEvidence.LoginUrl, 45000);
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && (ex is TimeoutException || ex is PlaywrightException)) {
                var reason = mainFailure != "unknown" ? mainFailure : ex is TimeoutException ? "document_commit_timeout" : "navigation_error";
                LogNavigation(executionId, "navigation_error", navigationClock.ElapsedMilliseconds, SafeDestination(page.Url), mainResponseStatus == 0 ? null : mainResponseStatus, reason);
                throw new InvalidOperationException("No se pudo recibir el documento principal del SRI (" + reason + ").");
            }
            finally {
                LogNavigation(executionId, "navigation_wait_finished", navigationClock.ElapsedMilliseconds, SafeDestination(page.Url), mainResponseStatus == 0 ? null : mainResponseStatus);
            }
            Log(executionId, "sri_navigation_completed");
            phase = "authentication";
            bool submitted = false, opened = false;
            for (var i = 0; i < 60; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (await SriAccessEvidence.HumanChallenge(page)) { Log(executionId, "human_challenge"); throw new HumanActionRequiredException("El SRI solicita una verificación humana. La operación quedó pendiente de intervención."); }
                if (await page.Locator("body").CountAsync() > 0 && await SriAccessEvidence.Matches(page, taxId))
                {
                    var sessionStorage = await page.EvaluateAsync<string>("() => JSON.stringify(Object.fromEntries(Object.entries(sessionStorage)))");
                    var session = new SessionData(account.Id, taxId, DateTimeOffset.UtcNow.AddMinutes(30), await context.StorageStateAsync(), sessionStorage);
                    vault.Save(session);
                    repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "Authenticated"; a.SessionVerified = true; a.VerifiedAt = DateTimeOffset.UtcNow; a.Expires = session.Expires; a.Message = "Acceso SRI verificado por el Worker."; return true; });
                    Log(executionId, "authentication_verified");
                    return session;
                }
                if (!opened && await InteractiveNavigation.IsOfficialDocument(page))
                {
                    var entry = page.GetByRole(AriaRole.Link, new() { Name = "Ir a iniciar sesión", Exact = true });
                    if (await entry.CountAsync() == 1 && await entry.IsVisibleAsync()) { opened = true; await entry.ClickAsync(); }
                }
                if (!submitted && await page.Locator("input[type=password]:visible").CountAsync() == 1)
                {
                    Log(executionId, "login_form_detected");
                    if (!CredentialVault.Configured().Present(account.Id)) throw new HumanActionRequiredException("Configure la credencial cifrada de la cuenta para acceder al SRI.");
                    var accepted = false;
                    await CredentialVault.Configured().Use(account.Id, taxId, async password => accepted = await AuthorizedLogin.Submit(page, taxId, password));
                    if (!accepted) throw new HumanActionRequiredException("No se reconoció un formulario seguro de acceso al SRI.");
                    submitted = true;
                    Log(executionId, "credentials_submitted");
                }
                await Task.Delay(1000, cancellation);
            }
            LogNavigation(executionId, "authentication_unverified", navigationClock.ElapsedMilliseconds, SafeDestination(page.Url), mainResponseStatus == 0 ? null : mainResponseStatus, resourceFailures > 0 ? "secondary_resource_errors" : "no_verified_login");
            throw new HumanActionRequiredException("No se pudo verificar el acceso al SRI. Revise la credencial o una posible validación humana antes de reanudar.");
        }
        catch (PlaywrightException) when (cancellation.IsCancellationRequested)
        {
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "Cancelled"; a.Message = "Autenticacion cancelada."; return true; });
            throw new OperationCanceledException(cancellation);
        }
        catch (HumanActionRequiredException ex)
        {
            Log(executionId, "human_action_required", phase);
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "RequiresHumanAction"; a.Message = ex.Message; return true; });
            throw;
        }
        catch (Exception ex)
        {
            Log(executionId, ClassifyFailure(ex, phase), phase);
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = cancellation.IsCancellationRequested ? "Cancelled" : "Failed"; a.Message = "El Worker no pudo verificar el acceso al SRI."; return true; });
            throw;
        }
    }
    public static Task<IResponse?> OpenDocument(IPage page, string url, float timeout) =>
        page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Commit, Timeout = timeout });

    // Only fixed event names and an execution ID are emitted: exception messages can contain secrets or URLs.
    private static void Log(string? executionId, string eventName, string? phase = null) =>
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { component = "sri_auth", executionId, eventName, phase }));

    // URLs may contain credentials or authorization codes, including in their path or host.
    public static string SafeDestination(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "srienlinea.sri.gob.ec" && uri.IsDefaultPort
        ? "https://srienlinea.sri.gob.ec" : "other_or_unavailable";

    public static string ClassifyNetworkFailure(string? failure)
    {
        if (failure is null) return "unknown";
        if (failure.Contains("ERR_CERT_", StringComparison.OrdinalIgnoreCase) || failure.Contains("ERR_SSL_", StringComparison.OrdinalIgnoreCase)) return "tls_error";
        if (failure.Contains("ERR_NAME_", StringComparison.OrdinalIgnoreCase)) return "dns_error";
        if (failure.Contains("TIMED_OUT", StringComparison.OrdinalIgnoreCase)) return "connection_timeout";
        if (failure.Contains("ERR_CONNECTION_", StringComparison.OrdinalIgnoreCase)) return "connection_error";
        if (failure.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase)) return "aborted";
        return "other_network_error";
    }

    public static string SafeResourceType(string? type) => type is "document" or "stylesheet" or "image" or "media" or "font" or "script" or "texttrack" or "xhr" or "fetch" or "eventsource" or "websocket" or "manifest" or "other" ? type : "other";

    private static void LogNavigation(string? executionId, string eventName, long elapsedMs, string destination, int? httpStatus = null, string? failure = null, string? resourceType = null) =>
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { component = "sri_auth", executionId, eventName, phase = "sri_navigation", elapsedMs, destination, httpStatus, failure, resourceType }));

    public static string ClassifyFailure(Exception ex, string phase)
    {
        if (ex is UnauthorizedAccessException) return "permission_error";
        if (ex is TimeoutException || ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) return "timeout";
        if (ex.Message.Contains("net::ERR_", StringComparison.OrdinalIgnoreCase)) return "connection_error";
        if (phase is "playwright_init" or "chromium_launch" or "browser_context") return "browser_start_error";
        if (ex is System.Security.Cryptography.CryptographicException or InvalidDataException) return "protected_data_error";
        return "unexpected_error";
    }
    public Task<bool> Tick(CancellationToken stop)
    {
        if (repository.Read(s => s.Authentications.Any(a => a.SessionVerified && a.Expires <= DateTimeOffset.UtcNow)))
            repository.Update(s => { foreach (var op in s.Authentications.Where(a => a.SessionVerified && a.Expires <= DateTimeOffset.UtcNow)) { op.SessionVerified = false; op.Expires = null; op.Status = "Expired"; } return true; });
        return Task.FromResult(false);
    }
}

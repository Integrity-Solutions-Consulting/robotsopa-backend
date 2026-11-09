using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Automation;
using SriAutomation.Domain;

namespace SriAutomation.LocalAgent;

internal static class RemoteWorker
{
    private static int completedReconciliationOffset;
    public static async Task RunAsync(CancellationToken cancellation = default)
    {
        var address = Environment.GetEnvironmentVariable("SRI_AGENT_API") ?? "https://botisc.dokploy.integritysolutions.com.ec/";
        var id = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_ID"); var secret = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !Guid.TryParse(id, out _) || secret is null || secret.Length != 64 || !secret.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Configure la API HTTPS y la identidad del agente.");
        Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET", null);
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation");
#if SOPA_SYNTHETIC_FIXTURE
        var testRoot = Environment.GetEnvironmentVariable("SOPA_SYNTHETIC_ROOT");
        var certHash = Environment.GetEnvironmentVariable("SOPA_SYNTHETIC_CERT_SHA256");
        if (!uri.IsLoopback || string.IsNullOrWhiteSpace(testRoot) || !Path.IsPathFullyQualified(testRoot) || certHash is null || certHash.Length != 64 || !certHash.All(Uri.IsHexDigit))
            throw new InvalidOperationException("La compilación sintética solo admite HTTPS loopback, directorio aislado y certificado fijado.");
        directory = Path.GetFullPath(testRoot);
#endif
        Directory.CreateDirectory(directory);
        using var singleton = new FileStream(Path.Combine(directory, "remote-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var httpHandler = new HttpClientHandler { AllowAutoRedirect = false };
#if SOPA_SYNTHETIC_FIXTURE
        httpHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null &&
            (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0 &&
            string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(certificate.RawData)), certHash, StringComparison.OrdinalIgnoreCase);
#endif
        using var client = new HttpClient(httpHandler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id); client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        // A legacy API returns 404. Other failures must not silently switch modes.
        using var capabilities = await client.GetAsync("/execution-agent/v1/capabilities", shutdown.Token);
        var central = false;
        var conditionalPending = false;
        if (capabilities.StatusCode != HttpStatusCode.NotFound)
        {
            capabilities.EnsureSuccessStatusCode();
            var supported = await capabilities.Content.ReadFromJsonAsync<Capabilities>(cancellationToken: shutdown.Token);
            central = supported?.CentralStorage == "v1";
            conditionalPending = central && supported?.ConditionalPending == true;
        }
        if (central) client.DefaultRequestHeaders.Add("X-SRI-Storage-Mode", "central-v1");
        using var deliveryStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var finishedReconciliation = central ? Task.Run(async () => {
            var pending = new PendingTransferQueue(Path.Combine(directory, "pending-transfers"));
            while (!deliveryStop.IsCancellationRequested)
            {
                try { await ReconcileCompletedPending(client, pending, deliveryStop.Token); }
                catch (OperationCanceledException) when (deliveryStop.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.Text.Json.JsonException or TaskCanceledException) { }
                try { await Task.Delay(TimeSpan.FromMinutes(1), deliveryStop.Token); }
                catch (OperationCanceledException) when (deliveryStop.IsCancellationRequested) { break; }
            }
        }) : Task.CompletedTask;
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SRI-Automation");
        var delivery = central ? Task.CompletedTask : Task.Run(async () => {
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
                    if (central && claim.StorageMode != "central-v1") throw new InvalidDataException("La API no confirmó el modo centralizado.");
                    await Execute(client, claim, shutdown.Token, directory, conditionalPending);
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
        finally { deliveryStop.Cancel(); await delivery; await finishedReconciliation; }
    }

    // Finalized work is confirmation-only: never upload or replace a current version.
    internal static async Task ReconcileCompletedPending(HttpClient client, PendingTransferQueue queue, CancellationToken cancellation)
    {
        var entries = queue.Entries();
        if (entries.Count == 0) return;
        var start = (int)((uint)Interlocked.Add(ref completedReconciliationOffset, 20) % (uint)entries.Count);
        foreach (var item in Enumerable.Range(0, Math.Min(20, entries.Count)).Select(i => entries[(start + i) % entries.Count]))
        {
            cancellation.ThrowIfCancellationRequested();
            var path = $"/execution-agent/v1/{Uri.EscapeDataString(item.JobId)}/confirmed?account={Uri.EscapeDataString(item.Account)}&type={Uri.EscapeDataString(item.Type)}&key={Uri.EscapeDataString(item.Key)}&issued={item.Issued:yyyy-MM-dd}&extension={Uri.EscapeDataString(item.Extension)}&generation={item.Generation}";
            using var response = await client.GetAsync(path, cancellation);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) continue;
            response.EnsureSuccessStatusCode();
            var confirmation = await response.Content.ReadFromJsonAsync<CompletedConfirmation>(cancellationToken: cancellation);
            var bytes = queue.Load(item.Account, item.Type, item.Key, item.Issued, item.Extension);
            if (bytes is not null && confirmation?.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)))
                queue.Confirm(item.Account, item.Type, item.Key, item.Issued, item.Extension, confirmation.Sha256);
        }
    }
    private sealed record CompletedConfirmation(string? Sha256);
    private static async Task Execute(HttpClient client, Claim claim, CancellationToken stopping, string directory, bool conditionalPending)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var trace = new DownloadDiagnosticTrace();
        trace.Start();
        var remote = new RemoteOwnedExecution(client, claim, trace.Write, cancellation: work.Token, transferQueue: new PendingTransferQueue(Path.Combine(directory, "pending-transfers")), supportsConditionalPending: conditionalPending);
        var renewal = Task.Run(async () => {
            try { while (!work.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(10), work.Token); if (!await remote.Renew(work.Token)) { trace.Write("LeaseRenewalNotConfirmed"); work.Cancel(); return; } } }
            catch (OperationCanceledException) when (work.IsCancellationRequested) { }
            catch (Exception ex) { trace.Write($"LeaseRenewalError type={ex.GetType().Name}"); work.Cancel(); }
        });
        var reconciliation = claim.StorageMode == "central-v1" ? Task.Run(async () => {
            try {
                while (!work.IsCancellationRequested) {
                    await Task.Delay(TimeSpan.FromMinutes(1), work.Token);
                    remote.ReconcilePending();
                }
            } catch (OperationCanceledException) when (work.IsCancellationRequested) { }
              catch (Exception ex) { trace.Write($"PendingReconciliationStopped type={ex.GetType().Name}"); }
        }) : Task.CompletedTask;
        try
        {
            var job = remote.Read(s => s.Executions.Single());
            var account = remote.Read(s => s.Accounts.Single()); var taxId = account.TaxId ?? account.Id;
            if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new HumanActionRequiredException("RUC invÃ¡lido.");
            remote.SetDeliveryIdentity(account.Id, taxId);
            if (claim.StorageMode == "central-v1") remote.ReconcilePending();
#if SOPA_SYNTHETIC_FIXTURE
            await SyntheticFixture.Execute(remote, job, directory, work.Token);
#else
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
            page = await AuthorizedQueryNavigation.Open(page, bound, taxId, work.Token, authenticate: p => remote.SubmitCredentials(p, taxId, work.Token), humanAction: p => WaitForHuman(p, remote, job.Id, work.Token), diagnostic: trace.Write);
            var profile = await ObservedPortalProfile.Discover(page, taxId, session: bound); profile.Validate();
            profile.TraceStep = trace.Write;
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
#endif
            if (claim.StorageMode == "central-v1") remote.ReconcilePending();
            using var finish = await remote.PostAsync("finish", work.Token); finish.EnsureSuccessStatusCode();
            var result = await finish.Content.ReadFromJsonAsync<FinishResult>(cancellationToken: work.Token);
            Console.WriteLine(result?.Status is "Completed" or "CompletedWithWarnings" ? "Trabajo finalizado; consulte los resultados en el panel." : "El backend terminÃ³ el procesamiento sin confirmar Ã©xito; revise el historial.");
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { trace.Write("ExecutionCancelledOrLeaseLost"); Console.Error.WriteLine("EjecuciÃ³n detenida por cancelaciÃ³n o pÃ©rdida de conexiÃ³n."); }
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
            if (ex is PortalRecognitionException recognition) trace.Write(AuthorizedQueryNavigation.SafeObservation("", recognition.Code));
            trace.Write($"ExecutionStopped type={ex.GetType().Name}");
            Console.Error.WriteLine("EjecuciÃ³n detenida; revise el historial y la ventana SRI.");
            try { using var result = await remote.PostAsync("stop", CancellationToken.None); } catch (Exception) { }
        }
        catch (Exception ex)
        {
            trace.Write($"ExecutionStopped type={ex.GetType().Name}");
            Console.Error.WriteLine("EjecuciÃ³n interrumpida; revise el historial.");
            try { using var result = await remote.PostAsync("stop", CancellationToken.None); } catch (Exception) { }
        }
        finally { work.Cancel(); await renewal; await reconciliation; }
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
    internal sealed record Claim(string JobId, Guid LeaseId, long Generation, string? StorageMode = null);
    private sealed record Capabilities(string? CentralStorage, bool ConditionalPending = false);
    private sealed record FinishResult(string Status);
}

internal sealed class RemoteOwnedExecution(HttpClient client, RemoteWorker.Claim claim, Action<string>? trace = null, string? deliveryRoot = null, string? deliveryLock = null, CancellationToken cancellation = default, PendingTransferQueue? transferQueue = null, bool supportsConditionalPending = false) : IStateRepository, IDownloadPipeline
{
    private PendingTransferQueue Queue => transferQueue ??= new PendingTransferQueue(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation", "pending-transfers"));
    private string? deliveryAccount;
    private string? deliveryTaxId;
    internal void SetDeliveryIdentity(string account, string taxId) { deliveryAccount = account; deliveryTaxId = taxId; }
    private string Path => "/execution-agent/v1/" + Validation.Identifier(claim.JobId);
    private HttpRequestMessage Request(HttpMethod method, string suffix)
    {
        var request = new HttpRequestMessage(method, Path + suffix);
        request.Headers.Add("X-Execution-Lease", claim.LeaseId.ToString()); request.Headers.Add("X-Execution-Generation", claim.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }
    private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token = default)
    {
        using (request)
        {
            var path = request.RequestUri?.OriginalString ?? "";
            var operation = path.EndsWith("/state") ? "state" : path.Contains("/available/") ? "availability" : path.EndsWith("/files") ? "files" : path.EndsWith("/renew") ? "renew" : "other";
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var response = await client.SendAsync(request, token);
                trace?.Invoke($"HttpTiming operation={operation} ms={timer.ElapsedMilliseconds} status={(int)response.StatusCode}");
                return response;
            }
            catch
            {
                trace?.Invoke($"HttpTiming operation={operation} ms={timer.ElapsedMilliseconds} failed=true");
                throw;
            }
        }
    }
    // Replay reads only. A lost response to a mutation does not prove it failed.
    private async Task<HttpResponseMessage> GetWithRetry(string suffix)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var response = await Send(Request(HttpMethod.Get, suffix), cancellation);
                var status = (int)response.StatusCode;
                if (status is not (408 or 429 or 500 or 502 or 503 or 504) || attempt == 2)
                {
                    if (attempt > 0 && response.IsSuccessStatusCode) trace?.Invoke("ConnectionRecovered");
                    return response;
                }
                trace?.Invoke($"ConnectionRetry http={status} attempt={attempt + 1}");
                response.Dispose();
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                cancellation.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && attempt < 2 &&
                (ex is HttpRequestException { StatusCode: null } or IOException or OperationCanceledException))
            {
                trace?.Invoke($"ConnectionRetry reason={(ex is OperationCanceledException ? "timeout" : "network")} attempt={attempt + 1}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << attempt)), cancellation);
        }
    }
    public async Task<bool> Renew(CancellationToken token)
    {
        using var response = await Send(Request(HttpMethod.Post, "/renew"), token);
        if (!response.IsSuccessStatusCode) { trace?.Invoke($"LeaseRenewalHttpStatus status={(int)response.StatusCode}"); return false; }
        var value = await response.Content.ReadFromJsonAsync<Renewal>(cancellationToken: token);
        if (value is { Cancelled: true }) trace?.Invoke("LeaseRenewalCancelled");
        else if (value is null) trace?.Invoke("LeaseRenewalEmptyResponse");
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
        using var response = GetWithRetry("/state").GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        return read(response.Content.ReadFromJsonAsync<State>().GetAwaiter().GetResult() ?? throw new IOException("Estado ausente."));
    }
    public T Update<T>(Func<State, T> mutate)
    {
        using var response = GetWithRetry("/state").GetAwaiter().GetResult(); response.EnsureSuccessStatusCode();
        var state = response.Content.ReadFromJsonAsync<State>().GetAwaiter().GetResult() ?? throw new IOException("Estado ausente.");
        var result = mutate(state); var request = Request(HttpMethod.Put, "/state"); request.Content = JsonContent.Create(state);
        using var update = Send(request).GetAwaiter().GetResult(); update.EnsureSuccessStatusCode(); return result;
    }
    public bool IsAvailable(Voucher document, string extension)
    {
        if (claim.StorageMode == "central-v1")
        {
            using var centralResponse = GetWithRetry("/central-available/" + Validation.Identifier(document.Id) + "/" + extension).GetAwaiter().GetResult();
            centralResponse.EnsureSuccessStatusCode();
            var available = centralResponse.Content.ReadFromJsonAsync<bool>(cancellationToken: cancellation).GetAwaiter().GetResult();
            if (available && centralResponse.Headers.TryGetValues("X-SRI-Content-SHA256", out var values))
            {
                var hash = values.SingleOrDefault();
                var pending = Pending(document.Account, document.Type, document.AccessKey, document.Issued, extension);
                if (pending is not null && hash == Hash(pending)) Queue.Confirm(document.Account, document.Type, document.AccessKey, document.Issued, extension, hash);
            }
            return available;
        }
        var taxId = document.Account == deliveryAccount && deliveryTaxId is not null
            ? deliveryTaxId
            : Read(s => { var account = s.Accounts.Single(a => a.Id == document.Account); return account.TaxId ?? account.Id; });
        var root = deliveryRoot ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SRI-Automation");
        if (LocalDocumentAvailability.IsAvailable(root, taxId, document, extension)) return true;
        using var response = GetWithRetry("/available/" + Validation.Identifier(document.Id) + "/" + extension).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (!response.Content.ReadFromJsonAsync<bool>().GetAwaiter().GetResult()) return false;
        // Serialize with the background delivery loop, which uses the same lock.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { DeliveryAgent.PollOnce(client, root, deliveryLock ?? DeliveryAgent.LockPath(), cancellation).GetAwaiter().GetResult(); }
            catch (IOException) { Task.Delay(250, cancellation).GetAwaiter().GetResult(); }
            if (LocalDocumentAvailability.IsAvailable(root, taxId, document, extension)) return true;
        }
        throw new IOException("No se pudo recuperar la copia disponible en el servidor.");
    }
    public Voucher Process(DownloadedVoucher file) => Process(file, false);
    private Voucher Process(DownloadedVoucher file, bool guarded)
    {
        if (claim.StorageMode == "central-v1")
        {
            if (file.Xml is not null) Preserve(file.Account, file.Type, file.Key, file.Issued, "xml", file.Xml);
            if (file.Pdf is not null) Preserve(file.Account, file.Type, file.Key, file.Issued, "pdf", file.Pdf);
        }
        var request = Request(HttpMethod.Post, "/files"); request.Content = JsonContent.Create(file);
        if (guarded) request.Headers.Add("X-SRI-Pending-Only-If-Missing-Or-Same", "1");
        trace?.Invoke("ARCHIVO_ENVIADO_API");
        HttpResponseMessage response;
        try { response = Send(request, cancellation).GetAwaiter().GetResult(); }
        catch (Exception ex) { trace?.Invoke($"FileTransportError type={ex.GetType().Name}"); throw; }
        using (response)
        {
            trace?.Invoke($"FileApiStatus status={(int)response.StatusCode}");
            response.EnsureSuccessStatusCode();
            try
            {
                var voucher = response.Content.ReadFromJsonAsync<Voucher>().GetAwaiter().GetResult() ?? throw new IOException("Documento no confirmado.");
                if (claim.StorageMode == "central-v1" &&
                    (voucher.Account != file.Account || voucher.Type != file.Type || voucher.AccessKey != file.Key || voucher.Issued != file.Issued ||
                     file.Xml is not null && (voucher.XmlPath is null || voucher.XmlSha256 != Hash(file.Xml)) ||
                     file.Pdf is not null && (voucher.PdfPath is null || voucher.PdfSha256 != Hash(file.Pdf))))
                    throw new IOException("La confirmación del servidor no coincide con los archivos enviados.");
                if (claim.StorageMode == "central-v1") {
                    if (file.Xml is not null) Queue.Confirm(file.Account, file.Type, file.Key, file.Issued, "xml", Hash(file.Xml));
                    if (file.Pdf is not null) Queue.Confirm(file.Account, file.Type, file.Key, file.Issued, "pdf", Hash(file.Pdf));
                }
                trace?.Invoke("ARCHIVO_CONFIRMADO_API");
                return voucher;
            }
            catch (Exception ex) { trace?.Invoke($"FileResponseError type={ex.GetType().Name}"); throw; }
        }
    }
    public byte[]? Pending(string account, string type, string key, DateOnly issued, string extension) =>
        claim.StorageMode == "central-v1" ? Queue.Load(account, type, key, issued, extension) : null;
    public void Preserve(string account, string type, string key, DateOnly issued, string extension, byte[] bytes)
    {
        if (claim.StorageMode == "central-v1") Queue.Save(account, type, key, issued, extension, bytes, claim.JobId, claim.Generation);
    }
    private readonly SemaphoreSlim reconciliationGate = new(1, 1);
    // Periodic passes only confirm existing copies; uploads run at safe points.
    internal void ReconcilePending(bool confirmOnly = false)
    {
        if (claim.StorageMode != "central-v1") return;
        if (!reconciliationGate.Wait(0)) return;
        try {
        foreach (var item in Queue.Entries().Where(e => e.JobId == claim.JobId && e.Generation <= claim.Generation).Take(20))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var state = Read(s => s);
                var job = state.Executions.Single();
                if (job.Id != claim.JobId || job.Status != "Running" || job.CancelRequested || item.Account != job.Query.Account ||
                    item.Issued < job.Query.From || item.Issued > job.Query.To || !job.Query.Types.Contains(item.Type) ||
                    !job.AttemptedKeys.Contains(item.Key) || (item.Extension == "xml" && !job.Query.Xml) ||
                    (item.Extension == "pdf" && !job.Query.Pdf)) continue;
                var bytes = Queue.Load(item.Account, item.Type, item.Key, item.Issued, item.Extension);
                if (bytes is null) continue;
                var doc = state.Documents.SingleOrDefault(d => d.Account == item.Account && d.Type == item.Type && d.AccessKey == item.Key && d.Issued == item.Issued);
                if (doc is not null && IsAvailable(doc, item.Extension))
                {
                    // A different server version is not authority to discard or overwrite this entry.
                    continue;
                }
                if (confirmOnly || !supportsConditionalPending) continue;
                Process(new(item.Account, item.Type, item.Key, item.Issued,
                    item.Extension == "xml" ? bytes : null, item.Extension == "pdf" ? bytes : null), true);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                trace?.Invoke($"PendingReconciliationDeferred type={ex.GetType().Name}");
                // Defer failures; do not keep retrying a lease or file in a tight loop.
                break;
            }
        }
        } finally { reconciliationGate.Release(); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    private sealed record Renewal(bool Cancelled);
    private sealed record RemoteCredential(string Username, string Password);
}


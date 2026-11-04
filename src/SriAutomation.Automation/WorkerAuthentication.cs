using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Automation;

// Authentication runs in the worker; challenges are reported without bypassing them.
public sealed class WorkerAuthentication(IStateRepository repository)
{
    public async Task<SessionData> Connect(Account account, CancellationToken cancellation, string? executionId = null)
    {
        if (account.IsTest) throw new HumanActionRequiredException("Las cuentas de prueba no pueden acceder al SRI real.");
        var taxId = account.TaxId ?? account.Id;
        var vault = SriPortal.Vault();
        SessionData? saved = null;
        try { saved = vault.Load(account.Id, taxId); } catch (HumanActionRequiredException) { }
        var op = repository.Update(s => {
            var operation = new AuthenticationOperation { Account = account.Id, ExecutionId = executionId, Owner = "Worker", Status = "OpeningBrowser", Message = "El Worker está comprobando el acceso al SRI." };
            s.Authentications.Add(operation);
            if (executionId is not null) ExecutionProgress.Set(s, executionId, "OpeningBrowser", operation.Message);
            return operation;
        });
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var context = await browser.NewContextAsync(new() { StorageState = saved?.StorageState });
            if (saved?.SessionStorage is not null) await context.AddInitScriptAsync("if(location.origin==='https://srienlinea.sri.gob.ec'){for(const [k,v] of Object.entries(JSON.parse(" + System.Text.Json.JsonSerializer.Serialize(saved.SessionStorage) + "))){sessionStorage.setItem(k,v)}}");
            var page = await context.NewPageAsync();
            using var registration = cancellation.Register(() => { _ = context.CloseAsync(); });
            await page.GotoAsync(SriAccessEvidence.LoginUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            bool submitted = false, opened = false;
            for (var i = 0; i < 60; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (await SriAccessEvidence.HumanChallenge(page)) throw new HumanActionRequiredException("El SRI solicita una verificación humana. La operación quedó pendiente de intervención.");
                if (await SriAccessEvidence.Matches(page, taxId))
                {
                    var sessionStorage = await page.EvaluateAsync<string>("() => JSON.stringify(Object.fromEntries(Object.entries(sessionStorage)))");
                    var session = new SessionData(account.Id, taxId, DateTimeOffset.UtcNow.AddMinutes(30), await context.StorageStateAsync(), sessionStorage);
                    vault.Save(session);
                    repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "Authenticated"; a.SessionVerified = true; a.VerifiedAt = DateTimeOffset.UtcNow; a.Expires = session.Expires; a.Message = "Acceso SRI verificado por el Worker."; return true; });
                    return session;
                }
                if (!opened && await InteractiveNavigation.IsOfficialDocument(page))
                {
                    var entry = page.GetByRole(AriaRole.Link, new() { Name = "Ir a iniciar sesión", Exact = true });
                    if (await entry.CountAsync() == 1 && await entry.IsVisibleAsync()) { opened = true; await entry.ClickAsync(); }
                }
                if (!submitted && await page.Locator("input[type=password]:visible").CountAsync() == 1)
                {
                    if (!CredentialVault.Configured().Present(account.Id)) throw new HumanActionRequiredException("Configure la credencial cifrada de la cuenta para acceder al SRI.");
                    var accepted = false;
                    await CredentialVault.Configured().Use(account.Id, taxId, async password => accepted = await AuthorizedLogin.Submit(page, taxId, password));
                    if (!accepted) throw new HumanActionRequiredException("No se reconoció un formulario seguro de acceso al SRI.");
                    submitted = true;
                }
                await Task.Delay(1000, cancellation);
            }
            throw new HumanActionRequiredException("No se pudo verificar el acceso al SRI. Revise la credencial o una posible validación humana antes de reanudar.");
        }
        catch (PlaywrightException) when (cancellation.IsCancellationRequested)
        {
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "Cancelled"; a.Message = "Autenticacion cancelada."; return true; });
            throw new OperationCanceledException(cancellation);
        }
        catch (HumanActionRequiredException ex)
        {
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = "RequiresHumanAction"; a.Message = ex.Message; return true; });
            throw;
        }
        catch (Exception)
        {
            repository.Update(s => { var a = s.Authentications.Single(x => x.Id == op.Id); a.Status = cancellation.IsCancellationRequested ? "Cancelled" : "Failed"; a.Message = "El Worker no pudo verificar el acceso al SRI."; return true; });
            throw;
        }
    }
    public Task<bool> Tick(CancellationToken stop)
    {
        if (repository.Read(s => s.Authentications.Any(a => a.SessionVerified && a.Expires <= DateTimeOffset.UtcNow)))
            repository.Update(s => { foreach (var op in s.Authentications.Where(a => a.SessionVerified && a.Expires <= DateTimeOffset.UtcNow)) { op.SessionVerified = false; op.Expires = null; op.Status = "Expired"; } return true; });
        return Task.FromResult(false);
    }
}

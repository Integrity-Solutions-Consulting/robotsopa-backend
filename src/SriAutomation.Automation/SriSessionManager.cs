using System.Text.RegularExpressions;
using Microsoft.Playwright;
using SriAutomation.Application;

namespace SriAutomation.Automation;

public sealed class SriSessionManager(PortalProfile profile)
{
    public async Task Ensure(IPage page, string taxId)
    {
        if(profile.RuntimeObserved){var check=profile.BoundSession is null?await SriAccessEvidence.Inspect(page,taxId):await profile.BoundSession.Inspect(page,taxId);if(!check.Verified)throw new PortalRecognitionException(check.Code);return;}
        if (!string.IsNullOrWhiteSpace(profile.HumanChallenge) && await page.Locator(profile.HumanChallenge).First.IsVisibleAsync())
            throw new HumanActionRequiredException("El SRI solicita verificación humana. Complete la sesión local y reanude la tarea.");
        if (page.Url.Contains("/auth/", StringComparison.OrdinalIgnoreCase) || await page.Locator(profile.Login).First.IsVisibleAsync())
            throw new HumanActionRequiredException("La sesión SRI expiró o no está autenticada. Complete el inicio de sesión local y reanude.");
        try { await page.Locator(profile.Authenticated).WaitForAsync(new() { State = WaitForSelectorState.Visible }); }
        catch (TimeoutException) { throw new HumanActionRequiredException("No se pudo confirmar una sesión SRI válida. Revise el portal localmente."); }
        var identity = await page.Locator(profile.AccountIdentity).InnerTextAsync();
        if (!Regex.IsMatch(identity, @"(?<!\d)" + Regex.Escape(taxId) + @"(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new HumanActionRequiredException("La sesión abierta no coincide con el RUC de la cuenta seleccionada. No se efectuó la consulta.");
    }
    public async Task Logout(IPage page)
    {
        if (!string.IsNullOrEmpty(profile.Logout)) await page.Locator(profile.Logout).ClickAsync();
        await page.Context.ClearCookiesAsync();
    }
}

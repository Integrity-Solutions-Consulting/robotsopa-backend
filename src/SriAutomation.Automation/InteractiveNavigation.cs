using Microsoft.Playwright;

namespace SriAutomation.Automation;

public static class InteractiveNavigation
{
    public static async Task<bool> IsOfficialDocument(IPage page)
    {
        if (page.IsClosed) return false;
        try { return await page.EvaluateAsync<string>("() => location.origin") == "https://srienlinea.sri.gob.ec"; }
        catch (PlaywrightException) { return false; }
    }
    public static async Task<bool> TryOpen(IPage page, string url, float timeout = 60000)
    {
        try
        {
            await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.Commit, Timeout = timeout });
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (PlaywrightException ex) when (ex.Message.Contains("net::ERR_", StringComparison.Ordinal)) { return false; }
        // La sesión interactiva conserva el contexto para que el usuario pueda revisar
        // el error y recargar normalmente. No se modifican TLS ni controles del portal.
    }
}

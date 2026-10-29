using Microsoft.Playwright;
using SriAutomation.Automation;
namespace SriAutomation.Tests;

public class PortalFixtureTests
{
    [BrowserFact]
    public async Task LocalPageObjectAndPagination()
    {
        // Opt-in: el navegador debe instalarse con el script Playwright antes.
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<table id='fixture-results'><tr><td>1</td></tr></table><button id='fixture-next' onclick=\"document.querySelector('td').textContent='2';this.disabled=true\">Siguiente</button>");
        var portal = new ConfiguredPage(page, new Dictionary<string, string> { ["results"] = "#fixture-results", ["next"] = "#fixture-next" });
        Assert.Contains("1", await portal.Control("results").InnerTextAsync());
        await portal.Control("next").ClickAsync();
        Assert.Contains("2", await portal.Control("results").InnerTextAsync());
        Assert.True(await portal.Control("next").IsDisabledAsync());
    }
}
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute() { if (Environment.GetEnvironmentVariable("SRI_TEST_BROWSER") != "1") Skip = "Requiere SRI_TEST_BROWSER=1 y Chromium instalado."; }
}

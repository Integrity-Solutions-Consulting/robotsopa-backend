using Microsoft.Playwright;
using SriAutomation.Automation;

namespace SriAutomation.Tests;

public sealed class InteractiveNavigationTests
{
    [BrowserFact]
    public async Task ConnectionFailureKeepsBrowserAvailableForManualRecovery()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.RouteAsync("https://fixture.invalid/**", route => route.AbortAsync("timedout"));
        Assert.False(await InteractiveNavigation.TryOpen(page, "https://fixture.invalid/"));
        Assert.True(browser.IsConnected);
        Assert.False(page.IsClosed);
        Assert.False(await InteractiveNavigation.IsOfficialDocument(page));
        var recoveryPage = await page.Context.NewPageAsync();
        await recoveryPage.SetContentAsync("<p>Recuperación local disponible</p>");
        Assert.Equal("Recuperación local disponible", await recoveryPage.Locator("p").InnerTextAsync());
    }
}

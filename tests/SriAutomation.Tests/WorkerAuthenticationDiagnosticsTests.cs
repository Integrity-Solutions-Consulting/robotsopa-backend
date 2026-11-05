using SriAutomation.Automation;
using Microsoft.Playwright;

namespace SriAutomation.Tests;

public class WorkerAuthenticationDiagnosticsTests
{
    [Theory]
    [InlineData("chromium_launch", "Executable doesn't exist", "browser_start_error")]
    [InlineData("sri_navigation", "net::ERR_CONNECTION_TIMED_OUT", "connection_error")]
    [InlineData("sri_navigation", "Timeout 45000ms exceeded", "timeout")]
    public void ClassifiesFailuresWithoutReturningExceptionDetails(string phase, string message, string expected)
    {
        Assert.Equal(expected, ServerWorkerAuthentication.ClassifyFailure(new Exception(message), phase));
    }

    [BrowserFact]
    public async Task DocumentCommitDoesNotWaitForBlockedScript()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("https://fixture.invalid/**", async route => {
            if (route.Request.Url.EndsWith("/blocked.js")) {
                await blocked.Task;
                await route.AbortAsync();
            } else await route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<html><head><script src='/blocked.js'></script></head><body>Ready</body></html>" });
        });
        try {
            var response = await ServerWorkerAuthentication.OpenDocument(page, "https://fixture.invalid/", 5000);
            Assert.Equal(200, response?.Status);
            Assert.Equal("https://fixture.invalid/", page.Url);
        } finally { blocked.TrySetResult(); }
    }

    [Theory]
    [InlineData("script", "script")]
    [InlineData("secret-token", "other")]
    public void ResourceTypesAreAllowlisted(string input, string expected) =>
        Assert.Equal(expected, ServerWorkerAuthentication.SafeResourceType(input));

    [Fact]
    public void ClassifiesPermissionFailures() => Assert.Equal("permission_error",
        ServerWorkerAuthentication.ClassifyFailure(new UnauthorizedAccessException(), "browser_context"));

    [Theory]
    [InlineData("https://srienlinea.sri.gob.ec/auth/secret?token=secret#secret", "https://srienlinea.sri.gob.ec")]
    [InlineData("https://secret.example/path", "other_or_unavailable")]
    [InlineData("http://srienlinea.sri.gob.ec/", "other_or_unavailable")]
    [InlineData("about:blank", "other_or_unavailable")]
    public void NavigationDestinationDoesNotExposeUrlData(string url, string expected) =>
        Assert.Equal(expected, ServerWorkerAuthentication.SafeDestination(url));

    [Theory]
    [InlineData("net::ERR_CERT_AUTHORITY_INVALID", "tls_error")]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", "dns_error")]
    [InlineData("net::ERR_CONNECTION_TIMED_OUT", "connection_timeout")]
    [InlineData("net::ERR_CONNECTION_REFUSED", "connection_error")]
    [InlineData("secret", "other_network_error")]
    public void NetworkFailuresUseFixedLabels(string failure, string expected) =>
        Assert.Equal(expected, ServerWorkerAuthentication.ClassifyNetworkFailure(failure));
}

using SriAutomation.Automation;

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

    [Fact]
    public void ClassifiesPermissionFailures() => Assert.Equal("permission_error",
        ServerWorkerAuthentication.ClassifyFailure(new UnauthorizedAccessException(), "browser_context"));
}

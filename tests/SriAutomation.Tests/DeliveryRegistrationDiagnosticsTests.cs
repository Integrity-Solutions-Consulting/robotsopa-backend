using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SriAutomation.Api;

namespace SriAutomation.Tests;

public sealed class DeliveryRegistrationDiagnosticsTests
{
    [Theory]
    [InlineData("HTTPS_NOT_RECOGNIZED")]
    [InlineData("ORIGIN_UNAUTHORIZED")]
    [InlineData("SESSION_INVALID")]
    [InlineData("ROLE_UNAUTHORIZED")]
    [InlineData("PASSWORD_CHANGE_REQUIRED")]
    public void LogsOnlyCauseAndFixedMessage(string cause)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/delivery-agents";
        context.Request.Headers.Cookie = "sri-session=secret-value";
        context.Request.Headers.Origin = "https://sensitive.example";
        var logger = new CaptureLogger();
        DeliveryRegistrationDiagnostics.Rejected(context, logger, cause);
        Assert.Contains(cause, logger.Message);
        Assert.DoesNotContain("secret-value", logger.Message);
        Assert.DoesNotContain("sensitive.example", logger.Message);
    }

    [Fact]
    public void IgnoresOtherRequests()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/delivery-agents";
        var logger = new CaptureLogger();
        DeliveryRegistrationDiagnostics.Rejected(context, logger, "SESSION_INVALID");
        Assert.Equal("", logger.Message);
    }

    private sealed class CaptureLogger : ILogger
    {
        public string Message { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Message = formatter(state, exception);
    }
}

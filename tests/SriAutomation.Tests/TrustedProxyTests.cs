using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SriAutomation.Api;

namespace SriAutomation.Tests;

public sealed class TrustedProxyTests
{
    [Theory]
    [InlineData("192.0.2.10", "https")]
    [InlineData("198.51.100.10", "http")]
    public async Task ForwardedSchemeIsAcceptedOnlyFromConfiguredProxy(string remoteIp, string expectedScheme)
    {
        var options = new ForwardedHeadersOptions();
        TrustedProxyConfiguration.Configure(options, [], ["192.0.2.0/24"]);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
        await middleware.Invoke(context);
        Assert.Equal(expectedScheme, context.Request.Scheme);
    }

    [Fact]
    public void InvalidProxyNetworkFailsClosed()
    {
        Assert.Throws<FormatException>(() => TrustedProxyConfiguration.Configure(new(), [], ["invalid"]));
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Configure(new(), [], ["0.0.0.0/0"]));
        Assert.Throws<InvalidOperationException>(() => TrustedProxyConfiguration.Configure(new(), [], ["::/0"]));
    }
}

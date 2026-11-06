using Microsoft.AspNetCore.Http;
using SriAutomation.Api;

namespace SriAutomation.Tests;

public class IsolatedTlsProxyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DefaultRejectsSpoofedScheme(string? setting)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        IsolatedTlsProxy.Apply(context, IsolatedTlsProxy.Configure(setting, true, false));
        Assert.False(context.Request.IsHttps);
    }

    [Fact]
    public void ExplicitModeUsesDeploymentAssertionNotClientHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        IsolatedTlsProxy.Apply(context, IsolatedTlsProxy.Configure("true", true, false));
        Assert.True(context.Request.IsHttps);
    }

    [Fact]
    public void InvalidOrUnsafeConfigurationFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => IsolatedTlsProxy.Configure("yes", true, false));
        Assert.Throws<InvalidOperationException>(() => IsolatedTlsProxy.Configure("true", false, false));
        Assert.Throws<InvalidOperationException>(() => IsolatedTlsProxy.Configure("true", true, true));
    }
}


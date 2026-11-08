using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SriAutomation.Api;

public static class TrustedProxyConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, IEnumerable<string> proxies, IEnumerable<string> networks)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
        foreach (var network in networks) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        options.ForwardLimit = 1;
    }
}

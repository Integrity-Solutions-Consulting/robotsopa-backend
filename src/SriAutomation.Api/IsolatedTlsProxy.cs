using Microsoft.AspNetCore.Http;

namespace SriAutomation.Api;

// This is a deployment assertion, not proof of network isolation.
public static class IsolatedTlsProxy
{
    public static bool Configure(string? value, bool secureCookie, bool hasTrustedProxies)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (!value.Equals("true", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SRI_TLS_PROXY_ISOLATED debe ser true o false.");
        if (!secureCookie || hasTrustedProxies) throw new InvalidOperationException("SRI_TLS_PROXY_ISOLATED requiere SRI_COOKIE_SECURE=true y no admite SRI_TRUSTED_PROXY_IPS ni SRI_TRUSTED_PROXY_NETWORKS.");
        return true;
    }

    public static void Apply(HttpContext context, bool enabled)
    {
        if (enabled) context.Request.Scheme = "https";
    }
}

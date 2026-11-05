namespace SriAutomation.Api;

// Prueba de identidad sin capacidad para reclamar ni modificar trabajos.
public static class RemoteAgentStatus
{
    public static void Map(IEndpointRouteBuilder endpoints, Func<HttpContext, string?> authenticate, bool executionAvailable = false)
    {
        endpoints.MapGet("/execution-agent/v1/status", (HttpContext context) =>
            authenticate(context) is null ? Results.Unauthorized() : Results.Ok(new { authenticated = true, executionAvailable }));
    }
}

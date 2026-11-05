using System.Net;
using System.Net.Http.Json;

namespace SriAutomation.LocalAgent;

// Reutiliza la identidad revocable de entrega únicamente para comprobar autenticación.
internal static class RemoteAgentProbe
{
    public static async Task<int> RunAsync()
    {
        var address = Environment.GetEnvironmentVariable("SRI_AGENT_API") ?? "https://botisc.dokploy.integritysolutions.com.ec/";
        var id = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_ID");
        var secret = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET");
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !Guid.TryParse(id, out _) || secret is null || secret.Length != 64 || !secret.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine("Configure SRI_AGENT_API HTTPS y las credenciales del agente de entrega mediante variables de entorno.");
            return 1;
        }
        Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET", null);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id);
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret);
        try
        {
            using var response = await client.GetAsync("/execution-agent/v1/status");
            if (response.StatusCode == HttpStatusCode.Unauthorized) { Console.Error.WriteLine("Identidad rechazada o revocada."); return 1; }
            if (!response.IsSuccessStatusCode) { Console.Error.WriteLine($"API respondió HTTP {(int)response.StatusCode}; compruebe la versión desplegada."); return 1; }
            var result = await response.Content.ReadFromJsonAsync<AgentStatus>();
            if (result?.Authenticated != true) { Console.Error.WriteLine("Respuesta de autenticación inválida."); return 1; }
            Console.WriteLine(result.ExecutionAvailable ? "Identidad autenticada y ejecución remota habilitada." : "Identidad autenticada; ejecución remota no habilitada.");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"No se pudo verificar la identidad ({ex.GetType().Name}).");
            return 1;
        }
    }
    private sealed record AgentStatus(bool Authenticated, bool ExecutionAvailable);
}

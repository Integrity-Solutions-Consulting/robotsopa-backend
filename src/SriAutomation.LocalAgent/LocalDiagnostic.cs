using Microsoft.Playwright;

namespace SriAutomation.LocalAgent;

// Diagnóstico de solo lectura: no envía credenciales ni solicita trabajos.
internal static class LocalDiagnostic
{
    public static async Task<int> RunAsync()
    {
        var apiText = Environment.GetEnvironmentVariable("SRI_AGENT_API") ?? "https://botisc.dokploy.integritysolutions.com.ec/";
        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var api) || api.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(api.UserInfo))
        {
            Console.Error.WriteLine("SRI_AGENT_API debe ser una URL HTTPS absoluta sin credenciales.");
            return 1;
        }
        var ok = true;
        Console.WriteLine("Comprobando API por HTTPS (sin autenticación ni cambios)...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await http.GetAsync(new Uri(api, "/health"));
            Console.WriteLine($"API /health: HTTP {(int)response.StatusCode}");
            ok &= response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"No se pudo conectar a la API por HTTPS ({ex.GetType().Name}).");
            ok = false;
        }

        Console.WriteLine("Abriendo la página pública del SRI en Edge...");
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge", Headless = false });
            var page = await browser.NewPageAsync();
            var response = await page.GotoAsync("https://srienlinea.sri.gob.ec/", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 45000 });
            Console.WriteLine($"SRI: HTTP {response?.Status.ToString() ?? "sin respuesta"}. Revise la ventana; se cerrará en 15 segundos.");
            ok &= response is not null && response.Ok;
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Console.Error.WriteLine($"No se pudo abrir la página pública del SRI con Edge ({ex.GetType().Name}).");
            ok = false;
        }
        return ok ? 0 : 1;
    }
}

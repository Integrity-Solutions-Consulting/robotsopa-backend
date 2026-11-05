using System.Net.Http.Json;
using System.Security.Cryptography;
using SriAutomation.Infrastructure;

namespace SriAutomation.LocalAgent;

internal static class DeliveryAgent
{
    public static async Task RunAsync(CancellationToken cancellation)
    {
        var url = Environment.GetEnvironmentVariable("SRI_DELIVERY_API");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("SRI_DELIVERY_API debe ser una URL HTTPS.");
        var id = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_ID");
        var secret = Environment.GetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET");
        if (!Guid.TryParse(id, out _) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Configure las credenciales del agente de entrega.");
        Environment.SetEnvironmentVariable("SRI_DELIVERY_AGENT_SECRET", null);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SRI-Automation");
        Directory.CreateDirectory(root);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Id", id);
        client.DefaultRequestHeaders.Add("X-Delivery-Agent-Secret", secret);
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                await PollOnce(client, root, LockPath(), cancellation);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { }
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
        }
    }

    internal static string LockPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRI-Automation", "delivery.lock");

    internal static async Task PollOnce(HttpClient client, string root, string lockPath, CancellationToken cancellation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        // Serializa también con el agente de entrega instalado anteriormente.
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var pending = await client.GetFromJsonAsync<PendingRemoteDelivery[]>("/delivery-agent/v1/pending", cancellation) ?? [];
        foreach (var item in pending)
        {
            try { await Deliver(client, root, item, cancellation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or HttpRequestException)
            {
                try { using var failure = await client.PostAsJsonAsync($"/delivery-agent/v1/{item.Id}/failure", new { code = "DeliveryFailed" }, cancellation); }
                catch (HttpRequestException) { }
            }
        }
    }

    private static async Task Deliver(HttpClient client, string root, PendingRemoteDelivery item, CancellationToken cancellation)
    {
        var storage = new DocumentStorage(root);
        var destination = storage.Resolve(item.DestinationPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        destination = storage.Resolve(item.DestinationPath.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await client.GetByteArrayAsync($"/delivery-agent/v1/{item.Id}/file", cancellation);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Hash de entrega inválido.");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellation);
            if (File.Exists(destination))
            {
                var existing = await File.ReadAllBytesAsync(destination, cancellation);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
                {
                    if (!string.Equals(item.Kind, "xlsx", StringComparison.Ordinal)) throw new IOException("Existe un archivo diferente en el destino.");
                    File.Move(temp, destination, true);
                }
            }
            else File.Move(temp, destination);
            using var confirmation = await client.PostAsJsonAsync($"/delivery-agent/v1/{item.Id}/confirm", new { sha256 = item.Sha256 }, cancellation);
            confirmation.EnsureSuccessStatusCode();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
        HideDeliveryState(root);
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

    internal static async Task Deliver(HttpClient client, string root, PendingRemoteDelivery item, CancellationToken cancellation)
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
            if (item.Kind is "pdf" or "xml") RecordGeneration(root, item);
            if (File.Exists(destination))
            {
                var existing = await File.ReadAllBytesAsync(destination, cancellation);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(bytes)))
                {
                    File.Move(temp, destination, true);
                }
            }
            else File.Move(temp, destination);
            using var confirmation = await client.PostAsJsonAsync($"/delivery-agent/v1/{item.Id}/confirm", new { sha256 = item.Sha256 }, cancellation);
            confirmation.EnsureSuccessStatusCode();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    // Called while the shared delivery lock is held. Persist the high-water mark
    // before replacing the file, so a crash cannot enable an older overwrite.
    internal static void RecordGeneration(string root, PendingRemoteDelivery item)
    {
        if (item.Generation < 1) throw new InvalidDataException("La API no proporciona generación de documento.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.DestinationPath)));
        var directory = Path.Combine(root, ".delivery-state");
        Directory.CreateDirectory(directory);
        HideDeliveryState(root);
        var path = Path.Combine(directory, key + ".json");
        if (File.Exists(path))
        {
            var prior = System.Text.Json.JsonSerializer.Deserialize<DeliveryGeneration>(File.ReadAllText(path)) ?? throw new InvalidDataException("Estado de entrega inválido.");
            if (prior.Generation > item.Generation || prior.Generation == item.Generation && !string.Equals(prior.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Entrega histórica rechazada.");
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new DeliveryGeneration(item.Generation, item.Sha256)));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void HideDeliveryState(string root)
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, ".delivery-state");
        try
        {
            if (Directory.Exists(directory)) File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Presentation must never block deliveries. */ }
    }
    private sealed record DeliveryGeneration(long Generation, string Sha256);
}

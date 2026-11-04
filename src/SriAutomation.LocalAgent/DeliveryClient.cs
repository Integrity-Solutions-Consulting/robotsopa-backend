using System.Net.Http.Json;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.LocalAgent;

public sealed record DeliveryPayload(string Id, string TaxId, string Extension, string Sha256, Voucher Document);
public sealed class DeliveryClient(HttpClient client, WindowsDesktopStorage storage)
{
    public async Task Tick(CancellationToken cancellation)
    {
        using var poll = await client.GetAsync("/api/internal/agent/deliveries/next", cancellation);
        if (poll.StatusCode == System.Net.HttpStatusCode.NoContent) return;
        poll.EnsureSuccessStatusCode();
        var item = await poll.Content.ReadFromJsonAsync<DeliveryPayload>(cancellation);
        if (item is null) return;
        try {
            var bytes = await client.GetByteArrayAsync("/api/internal/agent/deliveries/" + item.Id + "/content", cancellation);
            storage.Save(item.TaxId, item.Document, item.Extension, bytes, item.Sha256);
            using var response = await client.PostAsJsonAsync("/api/internal/agent/deliveries/" + item.Id + "/ack", new { item.Sha256 }, cancellation);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
            using var response = await client.PostAsync("/api/internal/agent/deliveries/" + item.Id + "/error", null, cancellation);
            response.EnsureSuccessStatusCode();
        }
    }
}

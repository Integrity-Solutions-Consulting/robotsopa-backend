using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SriAutomation.Infrastructure;

public sealed record SharePointOptions(string TenantId, string ClientId, string ClientSecret, string SiteId, string DriveId, string Folder)
{
    public override string ToString() => "SharePointOptions (credencial protegida)";
    public static SharePointOptions FromEnvironment()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Falta {name}.");
        var secretFile = Environment.GetEnvironmentVariable("SRI_SHAREPOINT_CLIENT_SECRET_FILE");
        var secret = !string.IsNullOrWhiteSpace(secretFile) ? File.ReadAllText(secretFile).TrimEnd('\r', '\n') : Required("SRI_SHAREPOINT_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("La credencial de SharePoint está vacía.");
        return new(Required("SRI_SHAREPOINT_TENANT_ID"), Required("SRI_SHAREPOINT_CLIENT_ID"), secret, Required("SRI_SHAREPOINT_SITE_ID"), Required("SRI_SHAREPOINT_DRIVE_ID"), Environment.GetEnvironmentVariable("SRI_SHAREPOINT_FOLDER") ?? "");
    }
}

public sealed class SharePointGraphException(string message, bool transient, TimeSpan? retryAfter = null) : Exception(message)
{
    public bool Transient { get; } = transient;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class SharePointGraphClient(HttpClient http, SharePointOptions options)
{
    private static readonly Uri Graph = new("https://graph.microsoft.com/v1.0/");
    private string? token;
    private DateTimeOffset tokenExpiry;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string PathPart(string path) => string.Join('/', path.Split('/').Select(Segment));
    private string Drive => $"drives/{Segment(options.DriveId)}";

    private async Task<string> Token(CancellationToken cancellation)
    {
        await tokenGate.WaitAsync(cancellation);
        try
        {
            if (token is not null && tokenExpiry > DateTimeOffset.UtcNow.AddMinutes(2)) return token;
            using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret, ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" });
            using var response = await http.PostAsync($"https://login.microsoftonline.com/{Segment(options.TenantId)}/oauth2/v2.0/token", body, cancellation);
            await Ensure(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellation));
            token = json.RootElement.GetProperty("access_token").GetString() ?? throw new SharePointGraphException("Respuesta de autenticación inválida.", false);
            tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32());
            return token;
        }
        finally { tokenGate.Release(); }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, new Uri(Graph, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token(cancellation));
        request.Content = body;
        return await http.SendAsync(request, cancellation);
    }

    private static Task Ensure(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        var status = (int)response.StatusCode;
        var delay = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is { } date) delay = date - DateTimeOffset.UtcNow;
        throw new SharePointGraphException($"Microsoft Graph respondió HTTP {status}.", status is 408 or 429 or >= 500, delay);
    }

    public async Task<byte[]?> ReadIfExists(string remotePath, CancellationToken cancellation)
    {
        using var response = await Send(HttpMethod.Get, $"{Drive}/root:/{PathPart(remotePath)}:/content", null, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await Ensure(response);
        return await response.Content.ReadAsByteArrayAsync(cancellation);
    }

    public async Task Upload(string remotePath, byte[] content, string hash, CancellationToken cancellation)
    {
        // A drive identifier alone is insufficient: confirm it belongs to the configured site.
        using (var siteDrive = await Send(HttpMethod.Get, $"drives/{Segment(options.DriveId)}/root?$select=sharepointIds", null, cancellation))
        {
            await Ensure(siteDrive);
            using var info = JsonDocument.Parse(await siteDrive.Content.ReadAsStreamAsync(cancellation));
            var configured = options.SiteId.Split(',');
            if (!info.RootElement.TryGetProperty("sharepointIds", out var ids) || !ids.TryGetProperty("siteId", out var siteId) || !string.Equals(siteId.GetString(), configured.Length == 3 ? configured[1] : options.SiteId, StringComparison.OrdinalIgnoreCase) ||
                (configured.Length == 3 && (!ids.TryGetProperty("webId", out var webId) || !string.Equals(webId.GetString(), configured[2], StringComparison.OrdinalIgnoreCase))))
                throw new SharePointGraphException("La biblioteca no pertenece al sitio configurado.", false);
        }
        var existing = await ReadIfExists(remotePath, cancellation);
        if (existing is not null)
        {
            if (DownloadPipeline.Hash(existing) != hash) throw new SharePointGraphException("Conflicto: el archivo remoto tiene contenido diferente.", false);
            return;
        }
        var parts = remotePath.Split('/');
        var current = "";
        foreach (var part in parts[..^1])
        {
            var next = current.Length == 0 ? part : current + "/" + part;
            using var check = await Send(HttpMethod.Get, $"{Drive}/root:/{PathPart(next)}", null, cancellation);
            if (check.StatusCode == HttpStatusCode.NotFound)
            {
                var parent = current.Length == 0 ? $"{Drive}/root/children" : $"{Drive}/root:/{PathPart(current)}:/children";
                using var create = await Send(HttpMethod.Post, parent, JsonContent.Create(new Dictionary<string, object> { ["name"] = part, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }), cancellation);
                if (create.StatusCode != HttpStatusCode.Conflict) await Ensure(create);
            }
            else await Ensure(check);
            current = next;
        }
        using var session = await Send(HttpMethod.Post, $"{Drive}/root:/{PathPart(remotePath)}:/createUploadSession", JsonContent.Create(new { item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail" } }), cancellation);
        if (session.StatusCode == HttpStatusCode.Conflict)
        {
            var competing = await ReadIfExists(remotePath, cancellation);
            if (competing is not null && DownloadPipeline.Hash(competing) == hash) return;
            throw new SharePointGraphException("Conflicto: el archivo remoto tiene contenido diferente.", false);
        }
        await Ensure(session);
        using var json = JsonDocument.Parse(await session.Content.ReadAsStreamAsync(cancellation));
        var uploadUrl = json.RootElement.GetProperty("uploadUrl").GetString();
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0) throw new SharePointGraphException("URL de carga inválida.", false);
        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, content.Length - 1, content.Length);
        using var uploaded = await http.SendAsync(request, cancellation);
        if (uploaded.StatusCode == HttpStatusCode.Conflict)
        {
            var competing = await ReadIfExists(remotePath, cancellation);
            if (competing is not null && DownloadPipeline.Hash(competing) == hash) return;
            throw new SharePointGraphException("Conflicto: el archivo remoto tiene contenido diferente.", false);
        }
        await Ensure(uploaded);
        var verified = await ReadIfExists(remotePath, cancellation);
        if (verified is null || DownloadPipeline.Hash(verified) != hash) throw new SharePointGraphException("No se pudo verificar la integridad del archivo remoto.", true);
    }
}

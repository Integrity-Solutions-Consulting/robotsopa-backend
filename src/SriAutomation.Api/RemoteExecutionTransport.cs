using Microsoft.AspNetCore.Http.Features;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

// Two 20 MB files expand to 53,333,336 Base64 bytes. The remaining space is
// bounded JSON metadata; other endpoints retain the server's 20 MB limit.
public static class RemoteExecutionTransport
{
    public const long FileRequestLimit = 54_000_000;

    public static bool IsFileUpload(HttpRequest request)
    {
        var parts = (request.Path.Value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return HttpMethods.IsPost(request.Method) && parts.Length == 4 &&
            parts[0] == "execution-agent" && parts[1] == "v1" && parts[3] == "files";
    }

    public static async Task Invoke(HttpContext context, RequestDelegate next, ILogger logger)
    {
        if (!context.Request.Path.StartsWithSegments("/execution-agent/v1")) { await next(context); return; }
        if (IsFileUpload(context.Request) && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = FileRequestLimit;
        string? reason = null;
        try { await next(context); }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, code) = ex switch
            {
                ExecutionLeaseException => (409, "LEASE_EXPIRED_OR_CHANGED"),
                BadHttpRequestException bad => (bad.StatusCode, bad.StatusCode == 413 ? "REQUEST_TOO_LARGE" : "INVALID_REQUEST"),
                DocumentSizeException => (400, "FILE_SIZE_INVALID"),
                RemoteFileScopeException => (400, "FILE_OUTSIDE_AUTHORIZED_JOB"),
                System.Xml.XmlException => (400, "XML_INVALID"),
                ArgumentException or InvalidDataException => (400, "DOCUMENT_OR_REQUEST_INVALID"),
                KeyNotFoundException => (404, "RESOURCE_NOT_FOUND"),
                IOException or UnauthorizedAccessException => (503, "STORAGE_UNAVAILABLE"),
                _ => (500, "INTERNAL_ERROR")
            };
            reason = code;
            await Results.Problem(code, statusCode: status).ExecuteAsync(context);
        }
        finally
        {
            if (context.Response.StatusCode >= 400)
            {
                var operation = IsFileUpload(context.Request) ? "files" :
                    context.Request.Path.Value?.Split('/').LastOrDefault() switch
                    { "claim" => "claim", "renew" => "renew", "state" => "state", "finish" => "finish", "stop" => "stop", "credentials" => "credentials", _ => "other" };
                logger.LogWarning("Remote execution rejected: operation={Operation} status={Status} reason={Reason}",
                    operation, context.Response.StatusCode, reason ?? (context.Response.StatusCode == 409 ? "LEASE_CONFLICT" : "HTTP_REJECTED"));
            }
        }
    }
}

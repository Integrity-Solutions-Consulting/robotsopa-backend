using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class DocumentExplorerEndpoints
{
    public static void Map(RouteGroupBuilder api, IPanelDatabase database)
    {
        bool Authorized(HttpContext c, string account, IStateRepository r) =>
            (c.Items["PanelRole"] as string == "admin" || database.AssignedAccounts((string)c.Items["PanelUser"]!).Contains(account)) &&
            r.Read(s => DataScope.Visible(s, account, c.Request.Headers["X-SRI-Test-Data"] == "1"));
        api.MapPost("/document-explorer/{account}/zip", (string account, DocumentZipService.Request input, HttpContext c, IStateRepository r, DocumentZipService zip) => {
            if (!Authorized(c, account, r)) return Results.StatusCode(403);
            return Results.Ok(zip.Start((string)c.Items["PanelUser"]!, account, input));
        });
        api.MapGet("/document-explorer/{account}/zip/{id}", (string account, string id, HttpContext c, IStateRepository r, DocumentZipService zip) => {
            if (!Authorized(c, account, r)) return Results.StatusCode(403);
            c.Response.Headers.CacheControl = "no-store";
            return Results.Ok(zip.Status((string)c.Items["PanelUser"]!, account, id));
        });
        api.MapGet("/document-explorer/{account}/zip/{id}/file", (string account, string id, HttpContext c, IStateRepository r, DocumentZipService zip) => {
            if (!Authorized(c, account, r)) return Results.StatusCode(403);
            var result = zip.Download((string)c.Items["PanelUser"]!, account, id);
            if (result is Microsoft.AspNetCore.Http.HttpResults.PhysicalFileHttpResult or Microsoft.AspNetCore.Http.HttpResults.FileStreamHttpResult)
                r.Update(s => { AuditTrail.Add(s, (string)c.Items["PanelUser"]!, "Descarga ZIP", account, "Descarga solicitada"); return true; });
            return result;
        });
        api.MapGet("/document-explorer/{account}", (string account, HttpContext c, IStateRepository repository) =>
        {
            if (c.Items["PanelRole"] as string != "admin" && !database.AssignedAccounts((string)c.Items["PanelUser"]!).Contains(account)) return Results.StatusCode(403);
            var showTests = c.Request.Headers["X-SRI-Test-Data"] == "1";
            return repository.Read(s => {
                if (!DataScope.Visible(s, account, showTests)) return Results.NotFound();
                var documents = s.Documents.Where(d => d.Account == account).ToList();
                var folders = documents.GroupBy(d => new { d.Issued.Year, d.Issued.Month, d.Type })
                    .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Type, Count = g.Count() }).ToArray();
                var reports = s.Executions.Where(e => e.Query.Account == account && (e.Status is "Completed" or "CompletedWithWarnings"))
                    .SelectMany(e => e.Reports).Select(r => new { r.Year, r.Month })
                    .Concat(s.PublishedReports.Where(r => r.Account == account).Select(r => new { r.Year, r.Month })).Distinct().ToArray();
                return Results.Ok(new { folders, reports });
            });
        });
        api.MapGet("/document-explorer/{account}/items", (string account, int year, int month, string type, string? search, string? sort, int? page, HttpContext c, IStateRepository repository) =>
        {
            if (c.Items["PanelRole"] as string != "admin" && !database.AssignedAccounts((string)c.Items["PanelUser"]!).Contains(account)) return Results.StatusCode(403);
            if (!Validation.Types.Contains(type)) return Results.BadRequest();
            _ = new DateOnly(year, month, 1);
            var showTests = c.Request.Headers["X-SRI-Test-Data"] == "1";
            return repository.Read(s => {
                if (!DataScope.Visible(s, account, showTests)) return Results.NotFound();
                var query = s.Documents.Where(d => d.Account == account && d.Issued.Year == year && d.Issued.Month == month && d.Type == type);
                if (!string.IsNullOrWhiteSpace(search)) query = query.Where(d => (d.Number ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) || (d.IssuerName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) || (d.Recipient ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) || d.AccessKey.Contains(search, StringComparison.OrdinalIgnoreCase));
                query = sort switch { "number" => query.OrderBy(d => d.Number), "total" => query.OrderByDescending(d => d.Total), _ => query.OrderByDescending(d => d.Issued) };
                var total = query.Count();
                var items = query.Skip((Math.Max(1, page ?? 1) - 1) * 50).Take(50).Select(d => new {
                    d.Id, d.Type, d.Number, d.AccessKey, d.Issued, d.AuthorizedAt, d.Authorization, d.IssuerName, d.IssuerTaxId,
                    d.Recipient, d.Identification, d.Subtotal, d.Discount, d.Taxes, d.Total, d.Status, d.Warning,
                    PdfRegistered = d.PdfPath != null, XmlRegistered = d.XmlPath != null
                }).ToArray();
                return Results.Ok(new { total, items });
            });
        });
    }
}

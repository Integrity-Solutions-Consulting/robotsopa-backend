using SriAutomation.Application;
using SriAutomation.Domain;
namespace SriAutomation.Infrastructure;

public static class DashboardData
{
    public static object Build(State state, IStorageProvider storage, bool includeTests = false, ISet<string>? allowed = null)
    {
        var documents = state.Documents.Where(d => DataScope.Visible(state, d.Account, includeTests) && (allowed is null || allowed.Contains(d.Account))).ToList();
        var executions = state.Executions.Where(e => DataScope.Visible(state, e.Query.Account, includeTests) && (allowed is null || allowed.Contains(e.Query.Account))).ToList();
        bool Exists(string? path) { try { return path is not null && File.Exists(storage.Resolve(path)); } catch (ArgumentException) { return false; } }
        return new
        {
            documents = documents.Count,
            downloaded = documents.Sum(d => (Exists(d.XmlPath) ? 1 : 0) + (Exists(d.PdfPath) ? 1 : 0)),
            completed = executions.Count(e => e.Status is "Completed" or "CompletedWithWarnings"),
            failed = executions.Count(e => e.Status == "Failed"),
            last = executions.OrderByDescending(e => e.Created).FirstOrDefault(),
            next = state.Schedules.Where(x => x.Enabled && DataScope.Visible(state, x.Query.Account, includeTests) && (allowed is null || allowed.Contains(x.Query.Account))).OrderBy(x => x.Next).FirstOrDefault()?.Next,
            workerOnline = state.WorkerHeartbeat > DateTimeOffset.UtcNow.AddSeconds(-15),
            recent = executions.OrderByDescending(e => e.Created).Take(5).ToList(),
            sriDocuments = documents.Count(d => d.Source == "Sri"),
            importedDocuments = documents.Count(d => d.Source != "Sri"),
            scope = includeTests ? "Incluye datos de prueba" : "Datos operativos; pruebas excluidas"
        };
    }
}

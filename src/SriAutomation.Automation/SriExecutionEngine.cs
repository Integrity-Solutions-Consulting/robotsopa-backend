using System.Globalization;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Automation;

public sealed class SriExecutionEngine(IStateRepository repository, IDownloadPipeline downloads)
{
    public async Task Run(IPage page, PortalProfile profile, Execution execution, string taxId, CancellationToken cancellation)
    {
        var query = execution.Query;
        if (query.To > query.From.AddYears(1).AddDays(-1))
            throw new HumanActionRequiredException("El rango supera un año.");
        var session = new SriSessionManager(profile); var portal = new SriQueryPage(page, profile);
        await session.Ensure(page, taxId); await portal.Navigate(cancellation);
        var checkpoint = execution.SriCheckpoint;
        if (checkpoint?.Complete == true) return;
        DateTimeOffset? previousQuery = null;
        for (var date = query.From; date <= query.To; date = date.AddDays(1))
        {
            foreach (var type in query.Types)
            {
                if (checkpoint is not null && (date < checkpoint.Date || date == checkpoint.Date && Array.IndexOf(query.Types, type) < Array.IndexOf(query.Types, checkpoint.Type))) continue;
                if (execution.Queries.Any(q => q.Date == date && q.Type == type && q.Errors == 0 && q.Status is "Consultado" or "SinDocumentos")) continue;
                cancellation.ThrowIfCancellationRequested(); await session.Ensure(page, taxId);
                if (previousQuery.HasValue)
                {
                    var remaining = TimeSpan.FromMilliseconds(profile.MinQueryIntervalMs) - (DateTimeOffset.UtcNow - previousQuery.Value);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellation); // Limitación explícita de frecuencia, no espera de DOM.
                }
                Stage("Querying");
                await portal.Search(query, date, type, cancellation); previousQuery = DateTimeOffset.UtcNow;
                await session.Ensure(page, taxId);
                var previous = execution.Queries.SingleOrDefault(q => q.Date == date && q.Type == type);
                var count = 0; var downloadedCount = previous?.Downloaded ?? 0; var errors = previous?.Errors ?? 0; var pageNumber = 0;
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                var seenPages = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    var rows = await portal.Rows();
                    if (!seenPages.Add(string.Join("|", rows.Select(r => r.Key)))) throw new InvalidDataException("La paginación repitió una página; se detuvo para evitar un bucle.");
                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        if (!seenKeys.Add(row.Key)) continue;
                        count++;
                        Progress("Running");
                        // Tras reinicio se reconstruye la página JSF actual y se deduplica por clave,
                        // sin saltar índices que podrían haber cambiado de orden en el portal.
                        SaveCheckpoint(new(date, type, pageNumber, rowIndex));
                        cancellation.ThrowIfCancellationRequested(); await session.Ensure(page, taxId);
                        var existing = repository.Read(s => s.Documents.SingleOrDefault(d => d.Account == query.Account && d.Type == type && d.AccessKey == row.Key));
                        var needXml = query.Xml && (existing is null || !downloads.IsAvailable(existing, "xml"));
                        var needPdf = query.Pdf && (existing is null || !downloads.IsAvailable(existing, "pdf"));
                        if (!needXml && !needPdf) { SaveCheckpoint(new(date, type, pageNumber, rowIndex + 1)); continue; }
                        repository.Update(s => {
                            var e = s.Executions.Single(e => e.Id == execution.Id);
                            if (e.AttemptedKeys.Contains(row.Key)) return true;
                            e.AttemptedKeys.Add(row.Key); return true;
                        });
                        try
                        {
                            byte[]? xml = null, pdf = null;
                            Stage("Downloading");
                            var transferIssues = new List<string>();
                            if (needXml) try { xml = await portal.Download(row, "xml", cancellation); } catch (Exception ex) when (ex is IOException or TimeoutException) { transferIssues.Add("Descarga XML fallida."); }
                            if (needPdf) try { pdf = await portal.Download(row, "pdf", cancellation); } catch (Exception ex) when (ex is IOException or TimeoutException) { transferIssues.Add("Descarga PDF fallida."); }
                            Stage("Processing");
                            var document = downloads.Process(new(query.Account, type, row.Key, row.Issued, xml, pdf));
                            if (needXml && xml is null) transferIssues.Add("XML no disponible; documento parcial.");
                            if (needPdf && pdf is null) transferIssues.Add("PDF no disponible; documento parcial.");
                            if (transferIssues.Count > 0)
                            {
                                repository.Update(s => { var d = s.Documents.Single(d => d.Id == document.Id); d.Status = "Revisar"; d.Warning = string.Join(" ", d.Warning, string.Join(" ", transferIssues)); return true; });
                                errors++;
                            }
                            downloadedCount++;
                            repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.Downloaded++; e.Processed++; return true; });
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException)
                        {
                            errors++;
                            repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.Errors++; e.Issues.Add($"{date:yyyy-MM-dd} · {type} · fila {rowIndex + 1}: archivo ausente, inválido o no coincidente; revisar y reintentar."); return true; });
                            if (existing is not null) repository.Update(s => { var d = s.Documents.Single(d => d.Id == existing.Id); d.Status = "Revisar"; d.Warning = "No se pudo confirmar la integridad de los archivos solicitados. Se conservó el contenido existente."; return true; });
                        }
                        SaveCheckpoint(new(date, type, pageNumber, rowIndex + 1));
                        Progress("Running");
                    }
                    cancellation.ThrowIfCancellationRequested(); await session.Ensure(page, taxId);
                    if (!await portal.Next(cancellation)) break;
                    pageNumber++;
                }
                repository.Update(s =>
                {
                    var e = s.Executions.Single(e => e.Id == execution.Id);
                    e.Queries.RemoveAll(q => q.Date == date && q.Type == type);
                    e.Queries.Add(new(date, type, count, downloadedCount, errors, count == 0 ? "SinDocumentos" : errors == 0 ? "Consultado" : "ConErrores"));
                    e.Found = e.Queries.Sum(q => q.Found);
                    e.Message = count == 0 ? "Fecha consultada sin comprobantes." : "Consulta procesada; archivos disponibles registrados.";
                    return true;
                });
                void Progress(string status) => repository.Update(s =>
                {
                    var e = s.Executions.Single(e => e.Id == execution.Id);
                    e.Queries.RemoveAll(q => q.Date == date && q.Type == type);
                    e.Queries.Add(new(date, type, count, downloadedCount, errors, status));
                    e.Found = e.Queries.Sum(q => q.Found); return true;
                });
            }
        }
        SaveCheckpoint(new(query.To, query.Types[^1], 0, 0, true));
        void Stage(string stage) => repository.Update(s => { ExecutionProgress.Set(s,execution.Id,stage,stage switch{"Querying"=>"Consultando comprobantes en el SRI.","Downloading"=>"Descargando PDF.",_=>"Verificando y guardando los documentos."});return true; });
        void SaveCheckpoint(SriCheckpoint next) => repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.SriCheckpoint = next; e.Checkpoint = $"{next.Date:yyyy-MM-dd}/{next.Type}/p{next.Page + 1}/f{next.Row + 1}"; return true; });
    }
}

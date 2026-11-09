using System.Globalization;
using System.Diagnostics;
using Microsoft.Playwright;
using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Automation;

public sealed class SriExecutionEngine(IStateRepository repository, IDownloadPipeline downloads)
{
    public async Task Run(IPage page, PortalProfile profile, Execution execution, string taxId, CancellationToken cancellation)
    {
        if (repository is PostgresStateRepository) throw new InvalidOperationException("La ejecución PostgreSQL requiere propiedad de trabajo.");
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
                var queryTimer = Stopwatch.StartNew();
                await portal.Search(query, date, type, cancellation); previousQuery = DateTimeOffset.UtcNow;
                profile.TraceStep?.Invoke($"Timing queryMs={queryTimer.ElapsedMilliseconds}");
                await session.Ensure(page, taxId);
                var previous = execution.Queries.SingleOrDefault(q => q.Date == date && q.Type == type);
                var count = 0; var downloadedCount = previous?.Downloaded ?? 0; var errors = previous?.Errors ?? 0; var pageNumber = 0;
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                var seenPages = new HashSet<string>(StringComparer.Ordinal);
                while (true)
                {
                    var pageTimer = Stopwatch.StartNew();
                    var rows = await portal.Rows();
                    if (!seenPages.Add(string.Join("|", rows.Select(r => r.Key)))) throw new InvalidDataException("La paginación repitió una página; se detuvo para evitar un bucle.");
                    for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var rowTimer = Stopwatch.StartNew();
                        profile.TraceStep?.Invoke($"DownloadRow page={pageNumber + 1} row={rowIndex + 1}");
                        await portal.TraceRowStructure(row);
                        if (!seenKeys.Add(row.Key)) { profile.TraceStep?.Invoke("DownloadRowSkipped Duplicate"); continue; }
                        count++;
                        Progress("Running");
                        profile.TraceStep?.Invoke("DownloadRowProgressRecorded");
                        // Tras reinicio se reconstruye la página JSF actual y se deduplica por clave,
                        // sin saltar índices que podrían haber cambiado de orden en el portal.
                        SaveCheckpoint(new(date, type, pageNumber, rowIndex));
                        profile.TraceStep?.Invoke("DownloadRowCheckpointRecorded");
                        cancellation.ThrowIfCancellationRequested(); await session.Ensure(page, taxId);
                        profile.TraceStep?.Invoke("DownloadRowSessionConfirmed");
                        profile.TraceStep?.Invoke("DownloadRowCheckingAvailability");
                        Voucher? existing; bool needXml, needPdf;
                        var availabilityTimer = Stopwatch.StartNew();
                        try {
                            existing = repository.Read(s => s.Documents.SingleOrDefault(d => d.Account == query.Account && d.Type == type && d.AccessKey == row.Key));
                            needXml = query.Xml && (existing is null || !downloads.IsAvailable(existing, "xml"));
                            needPdf = query.Pdf && (existing is null || !downloads.IsAvailable(existing, "pdf"));
                        } catch (Exception ex) { profile.TraceStep?.Invoke($"DownloadAvailabilityError {ex.GetType().Name}"); throw; }
                        profile.TraceStep?.Invoke($"Timing availabilityMs={availabilityTimer.ElapsedMilliseconds}");
                        profile.TraceStep?.Invoke($"DownloadRowDecision pdfRequested={query.Pdf} xmlRequested={query.Xml} existing={existing is not null} pdfNeeded={needPdf} xmlNeeded={needXml}");
                        if (!needXml && !needPdf) { profile.TraceStep?.Invoke("DownloadRowSkipped NoFilesNeeded"); SaveCheckpoint(new(date, type, pageNumber, rowIndex + 1)); profile.TraceStep?.Invoke($"Timing rowMs={rowTimer.ElapsedMilliseconds}"); continue; }
                        profile.TraceStep?.Invoke("DownloadRowPreparingAttempt");
                        repository.Update(s => {
                            var e = s.Executions.Single(e => e.Id == execution.Id);
                            if (e.AttemptedKeys.Contains(row.Key)) return true;
                            e.AttemptedKeys.Add(row.Key); return true;
                        });
                        try
                        {
                            byte[]? xml = null, pdf = null;
                            Stage("Downloading");
                            var downloadTimer = Stopwatch.StartNew();
                            var transferIssues = new List<string>();
                            if (needXml) {
                                xml = downloads.Pending(query.Account, type, row.Key, row.Issued, "xml");
                                if (xml is null) try { xml = await portal.Download(row, "xml", cancellation); } catch (Exception ex) when (ex is IOException or TimeoutException) { transferIssues.Add("Descarga XML fallida."); }
                                if (xml is not null) downloads.Preserve(query.Account, type, row.Key, row.Issued, "xml", xml);
                            }
                            if (needPdf) {
                                pdf = downloads.Pending(query.Account, type, row.Key, row.Issued, "pdf");
                                if (pdf is null) try { pdf = await portal.Download(row, "pdf", cancellation); } catch (Exception ex) when (ex is IOException or TimeoutException) { transferIssues.Add("Descarga PDF fallida."); }
                                if (pdf is not null) downloads.Preserve(query.Account, type, row.Key, row.Issued, "pdf", pdf);
                            }
                            profile.TraceStep?.Invoke($"Timing downloadMs={downloadTimer.ElapsedMilliseconds}");
                            Stage("Processing");
                            var document = downloads.Process(new(query.Account, type, row.Key, row.Issued, xml, pdf));
                            profile.TraceStep?.Invoke($"DownloadFilesProcessed xml={xml is not null} pdf={pdf is not null}");
                            if (needXml && xml is null) transferIssues.Add("XML no disponible; documento parcial.");
                            if (needPdf && pdf is null) transferIssues.Add("PDF no disponible; documento parcial.");
                            if (transferIssues.Count > 0)
                            {
                                repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.Issues.Add($"{date:yyyy-MM-dd} · {type} · fila {rowIndex + 1}: no se obtuvieron todos los archivos solicitados."); return true; });
                                errors++;
                                repository.Update(s => { s.Executions.Single(e => e.Id == execution.Id).Errors++; return true; });
                            }
                            downloadedCount++;
                            repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.Downloaded++; e.Processed++; return true; });
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or System.Xml.XmlException)
                        {
                            profile.TraceStep?.Invoke($"DownloadProcessingError {ex.GetType().Name}");
                            errors++;
                            repository.Update(s => { var e = s.Executions.Single(e => e.Id == execution.Id); e.Errors++; e.Issues.Add($"{date:yyyy-MM-dd} · {type} · fila {rowIndex + 1}: archivo ausente, inválido o no coincidente; revisar y reintentar."); return true; });
                            if (existing is not null) repository.Update(s => { var d = s.Documents.Single(d => d.Id == existing.Id); d.Status = "Revisar"; d.Warning = "No se pudo confirmar la integridad de los archivos solicitados. Se conservó el contenido existente."; return true; });
                        }
                        SaveCheckpoint(new(date, type, pageNumber, rowIndex + 1));
                        Progress("Running");
                        profile.TraceStep?.Invoke($"Timing rowMs={rowTimer.ElapsedMilliseconds}");
                    }
                    profile.TraceStep?.Invoke($"Timing pageMs={pageTimer.ElapsedMilliseconds} rows={rows.Count}");
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

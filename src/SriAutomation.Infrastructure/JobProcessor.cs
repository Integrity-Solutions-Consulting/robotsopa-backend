using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class JobProcessor(IStateRepository repository, IStorageProvider storage, ISriComprobanteService sri)
{
    public void Recover()
    {
        repository.Update(s =>
        {
            foreach (var e in s.Executions.Where(e => e.Status == "Running"))
            {
                e.Status = e.CancelRequested ? "Cancelled" : e.Attempts >= 3 ? "Failed" : "Pending";
                e.Message = "Recuperada tras reinicio del Worker.";
                if (e.Status is "Cancelled" or "Failed") e.Finished = DateTimeOffset.UtcNow;
            }
            foreach(var op in s.Authentications.Where(a=>a.Owner=="Worker"&&a.Status is "OpeningBrowser" or "InspectingPortal")){op.Status="Failed";op.Command="";op.Message="Conexión interrumpida al reiniciar. Puede volver a conectar desde Configuración.";}
            return true;
        });
    }
    public async Task<bool> Tick(CancellationToken cancellation)
    {
        var execution = repository.Update(s =>
        {
            s.WorkerHeartbeat = DateTimeOffset.UtcNow;
            foreach (var schedule in s.Schedules.Where(x => x.Enabled && x.Next <= DateTimeOffset.UtcNow))
            {
                if (s.Executions.Any(e => e.Query.Account == schedule.Query.Account && e.Status is "Pending" or "Running")) continue;
                var scheduled=Validation.Enqueue(s, schedule.Query);scheduled.RequestedBy=schedule.CreatedBy;
                AuditTrail.Add(s,schedule.CreatedBy??"Sistema (programación)","Inicio de ejecución programada",schedule.Query.Account,"Encolada automáticamente por el sistema");
                do { schedule.Next = Validation.Next(schedule.Next, schedule.Frequency,schedule.IntervalDays); }
                while (schedule.Next <= DateTimeOffset.UtcNow);
                if (schedule.Frequency == "Once") schedule.Enabled = false;
            }
            foreach (var e in s.Executions.Where(e => e.Status == "Pending" && e.CancelRequested)) { e.Status = "Cancelled"; e.Finished = DateTimeOffset.UtcNow; }
            var job = s.Executions.FirstOrDefault(e => e.Status == "Pending");
            if (job is not null) { job.Status = "Running"; job.Started ??= DateTimeOffset.UtcNow; job.Attempts++; }
            return job;
        });
        if (execution is null) return false;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var watch = Task.Run(async () =>
        {
            while (!cancelled.IsCancellationRequested)
            {
                if (repository.Read(s => s.Executions.Single(e => e.Id == execution.Id).CancelRequested)) cancelled.Cancel();
                repository.Update(s => { s.WorkerHeartbeat = DateTimeOffset.UtcNow; return true; });
                try { await Task.Delay(1000, cancelled.Token); } catch (OperationCanceledException) { break; }
            }
        }, CancellationToken.None);
        try
        {
            if (execution.Query.Mode == "Sri") await sri.Execute(execution, cancelled.Token);
            if(execution.Query.Mode=="Sri"&&repository.Read(s=>s.Executions.Single(e=>e.Id==execution.Id).Queries.Where(q=>q.Status is "Consultado" or "SinDocumentos" or "ConErrores").Select(q=>(q.Date,q.Type)).Distinct().Count())<(execution.Query.To.DayNumber-execution.Query.From.DayNumber+1)*execution.Query.Types.Length)throw new InvalidDataException("El SRI no confirmó todas las consultas solicitadas.");
            repository.Update(s => { s.Executions.Single(e => e.Id == execution.Id).Stage = "Processing"; return true; });
            var documents = repository.Read(s => s.Documents.Where(d => d.Account == execution.Query.Account && d.Issued >= execution.Query.From && d.Issued <= execution.Query.To && execution.Query.Types.Contains(d.Type)).ToList());
            var months=documents.Select(d=>(d.Issued.Year,d.Issued.Month)).ToHashSet();

            foreach (var month in months.OrderBy(m=>m.Year).ThenBy(m=>m.Month))
            {
                cancelled.Token.ThrowIfCancellationRequested();
                var completeMonth = repository.Read(s => s.Documents.Where(d => d.Account == execution.Query.Account && d.Issued.Year == month.Year).ToList());
                repository.Update(s=>{ExecutionProgress.Set(s,execution.Id,"GeneratingExcel",$"Generando Excel {month.Year:D4}-{month.Month:D2}.");return true;});
                var report=storage.Report(execution.Query.Account, month.Year, month.Month, completeMonth);
                if(!File.Exists(storage.Resolve(report)))throw new IOException("No se pudo comprobar el Excel generado.");
                repository.Update(s=>{var job=s.Executions.Single(e=>e.Id==execution.Id);job.Reports.RemoveAll(r=>r.Year==month.Year&&r.Month==month.Month);job.Reports.Add(new(month.Year,month.Month,report));return true;});
                if (execution.Query.Mode == "Stored") repository.Update(s => { var job = s.Executions.Single(e => e.Id == execution.Id); job.Checkpoint = $"{month.Year:D4}-{month.Month:D2}"; job.Processed = documents.Count(d => d.Issued.Year < month.Year || d.Issued.Year == month.Year && d.Issued.Month <= month.Month); return true; });
            }
            repository.Update(s =>
            {
                var job = s.Executions.Single(e => e.Id == execution.Id);
                if (execution.Query.Mode == "Stored") { job.Found = documents.Count; job.Processed = documents.Count; }
                job.Errors = Math.Max(job.Errors, documents.Count(d => d.Status != "ValidadoXML"));
                job.Status = job.CancelRequested ? "Cancelled" : job.Errors > 0 ? "CompletedWithWarnings" : "Completed";
                job.Finished = DateTimeOffset.UtcNow; job.Message = execution.Query.Mode == "Sri" ? "Consultas finalizadas y reportes actualizados desde los archivos almacenados." : "Reportes generados desde documentos almacenados; sin consulta SRI.";
                job.Stage = job.Status;
                ExecutionProgress.Set(s,job.Id,job.Status,job.Message);
                AuditTrail.Add(s,job.RequestedBy??"Sistema (Worker)","Resultado de procesamiento",job.Query.Account,job.Status);
                return true;
            });
        }
        catch (HumanActionRequiredException ex) { PartialReports(); Finish("RequiresHumanAction", ex.Message); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { PartialReports(); Finish("Cancelled", "Cancelada por el usuario."); }
        catch (OperationCanceledException) { /* Permanece Running para recuperación al reiniciar. */ }
        catch (IOException ex) when (execution.Attempts < 3)
        {
            Console.Error.WriteLine("Reintento de almacenamiento: " + ex.GetType().Name);
            repository.Update(s => { var job = s.Executions.Single(e => e.Id == execution.Id); job.Status = "Pending"; job.Message = ex is SriUnavailableException ? "El SRI no responde; se reintentará desde el checkpoint (máximo 3 intentos)." : "Error transitorio de almacenamiento; reintento limitado (máximo 3 intentos)."; return true; });
        }
        catch (Exception ex) { Console.Error.WriteLine("Error de procesamiento: " + ex.GetType().Name); Finish("Failed", ex is SriUnavailableException ? "El SRI no respondió después de los intentos permitidos. Puede reintentar desde Historial." : ex is InvalidDataException ? "El SRI no confirmó todos los resultados o un archivo no superó la validación. No se declaró éxito." : "Error al procesar. Revise permisos, integridad y configuración del Worker."); }
        finally { cancelled.Cancel(); await watch; }
        return true;
        void Finish(string status, string message) => repository.Update(s => { var job = s.Executions.Single(e => e.Id == execution.Id); job.Status = status;ExecutionProgress.Set(s,job.Id,status,message);job.Finished = DateTimeOffset.UtcNow;AuditTrail.Add(s,job.RequestedBy??"Sistema (Worker)","Resultado de procesamiento",job.Query.Account,status); return true; });
        void PartialReports()
        {
            var docs = repository.Read(s => s.Documents.Where(d => d.Account == execution.Query.Account).ToList());
            foreach (var month in docs.Where(d => d.Issued >= execution.Query.From && d.Issued <= execution.Query.To).Select(d => (d.Issued.Year, d.Issued.Month)).Distinct())
                try { storage.Report(execution.Query.Account, month.Year, month.Month, docs); }
                catch (Exception ex) { Console.Error.WriteLine("Reporte pendiente: " + ex.GetType().Name); repository.Update(s => { s.Executions.Single(e => e.Id == execution.Id).Issues.Add("Reporte mensual pendiente: revise el almacenamiento y regenere desde Reportes."); return true; }); }
        }
    }
}

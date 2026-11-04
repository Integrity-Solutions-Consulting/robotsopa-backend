using SriAutomation.Application;
using SriAutomation.Domain;
using SriAutomation.Infrastructure;

namespace SriAutomation.Api;

public static class LocalDeliveryEndpoints
{
    public static void Queue(State s, Execution job, Voucher doc, string extension)
    {
        if (job.Query.StorageMode != "LOCAL_WINDOWS") return;
        if (doc.Source != "Sri") throw new ArgumentException("Solo se entregan comprobantes obtenidos del SRI.");
        var account = s.Accounts.Single(a => a.Id == job.Query.Account);
        var taxId = account.TaxId ?? account.Id;
        if (taxId.Length != 13 || !taxId.All(char.IsAsciiDigit)) throw new ArgumentException("RUC inválido.");
        var path = extension == "xml" ? doc.XmlPath : doc.PdfPath;
        var hash = extension == "xml" ? doc.XmlSha256 : doc.PdfSha256;
        if (path is null || hash is null) return;
        if (!s.LocalDeliveries.Any(x => x.ExecutionId == job.Id && x.DocumentId == doc.Id && x.Extension == extension))
        {
            s.LocalDeliveries.Add(new LocalDelivery { ExecutionId = job.Id, DocumentId = doc.Id, TaxId = taxId, Extension = extension, LocalPath = path, Sha256 = hash });
            job.DeliveryTotal++;
        }
    }
    public static void MapLocalDelivery(this WebApplication app)
    {
        var agent = app.MapGroup("/api/internal/agent/deliveries");
        agent.MapGet("/next", (IStateRepository r) => r.Update(s => {
            var item = s.LocalDeliveries.FirstOrDefault(x => x.Status == "Pending");
            if (item is null) return null;
            item.Attempts++;
            var document = s.Documents.Single(d => d.Id == item.DocumentId);
            return new DeliveryPayload(item.Id, item.TaxId, item.Extension, item.Sha256, document);
        }));
        agent.MapGet("/{id}/content", (string id, IStateRepository r, IStorageProvider storage) => {
            var item = r.Read(s => s.LocalDeliveries.Single(x => x.Id == id && x.Status == "Pending"));
            var bytes = File.ReadAllBytes(storage.Resolve(item.LocalPath));
            if (DownloadPipeline.Hash(bytes) != item.Sha256) throw new InvalidDataException("La huella del archivo pendiente no coincide.");
            return Results.File(bytes, "application/octet-stream");
        });
        agent.MapPost("/{id}/ack", (string id, DeliveryAck ack, IStateRepository r) => r.Update(s => {
            var item = s.LocalDeliveries.Single(x => x.Id == id);
            if (!string.Equals(item.Sha256, ack.Sha256, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Confirmación de integridad inválida.");
            item.Status = "Completed"; item.Completed ??= DateTimeOffset.UtcNow; item.Error = null;
            var job = s.Executions.Single(e => e.Id == item.ExecutionId);
            job.DeliveryCompleted = s.LocalDeliveries.Count(x => x.ExecutionId == job.Id && x.Status == "Completed"); job.DeliveryError = null;
            if (job.Status == "WaitingForDelivery" && s.LocalDeliveries.Where(x => x.ExecutionId == job.Id).All(x => x.Status == "Completed")) {
                job.Status = job.Errors > 0 ? "CompletedWithWarnings" : "Completed"; job.Stage = job.Status; job.Finished = DateTimeOffset.UtcNow;
                job.Message = "Archivos y resumen mensual confirmados por el agente Windows.";
                ExecutionProgress.Set(s, job.Id, job.Status, job.Message);
            }
            return new { saved = true };
        }));
        agent.MapPost("/{id}/error", (string id, IStateRepository r) => r.Update(s => {
            var item = s.LocalDeliveries.Single(x => x.Id == id);
            if (item.Status != "Completed") item.Error = "El agente no pudo guardar o verificar el archivo; reintentará.";
            return new { saved = true };
        }));
    }
    public sealed record DeliveryPayload(string Id, string TaxId, string Extension, string Sha256, Voucher Document);
    public sealed record DeliveryAck(string Sha256);
}

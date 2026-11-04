using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

public sealed class SharePointUploadProcessor(IStateRepository repository, IStorageProvider storage, SharePointGraphClient graph)
{
    public async Task<bool> Tick(CancellationToken cancellation)
    {
        var cleanup = repository.Read(s => s.SharePointUploads.FirstOrDefault(u => u.Status == "Completed" && !u.Cleaned && u.CleanupError is null && !s.LocalDeliveries.Any(d => d.LocalPath == u.LocalPath && d.Status != "Completed")));
        if (cleanup is not null)
        {
            try { await Cleanup(cleanup, cancellation); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                repository.Update(s => { var item = s.SharePointUploads.Single(u => u.Id == cleanup.Id); item.CleanupError = "No se pudo limpiar el temporal; se conservó para revisión."; AuditTrail.Add(s, item.User, "Publicación SharePoint", item.Account, $"{item.CleanupError} {item.RemotePath}"); return true; });
            }
            return true;
        }
        var now = DateTimeOffset.UtcNow;
        var lease = Guid.NewGuid().ToString("N");
        var upload = repository.Update(s =>
        {
            var item = s.SharePointUploads.FirstOrDefault(u => (u.Status == "Pending" && (u.NextAttempt is null || u.NextAttempt <= now)) || (u.Status == "Uploading" && u.LeaseUntil <= now));
            if (item is null) return null;
            item.Status = "Uploading"; item.LeaseId = lease; item.LeaseUntil = now.AddMinutes(10); item.Attempts++; item.Error = null;
            AuditTrail.Add(s, item.User, "Publicación SharePoint", item.Account, $"Intento {item.Attempts}: {item.RemotePath}");
            return item;
        });
        if (upload is null) return false;
        try
        {
            var file = storage.Resolve(upload.LocalPath);
            var bytes = await File.ReadAllBytesAsync(file, cancellation);
            if (DownloadPipeline.Hash(bytes) != upload.Sha256) throw new InvalidDataException("El archivo temporal no coincide con su huella.");
            await graph.Upload(upload.RemotePath, bytes, upload.Sha256, cancellation);
            var confirmed = repository.Update(s =>
            {
                var item = s.SharePointUploads.Single(u => u.Id == upload.Id);
                if (item.LeaseId != lease || item.Status != "Uploading") return false;
                item.Status = "Completed"; item.Completed = DateTimeOffset.UtcNow; item.LeaseId = null; item.LeaseUntil = null;
                AuditTrail.Add(s, item.User, "Publicación SharePoint", item.Account, $"Confirmada: {item.RemotePath}");
                return true;
            });
            // The next tick performs cleanup, including recovery after a process restart.
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var graphError = ex as SharePointGraphException;
            var retry = graphError?.Transient ?? (ex is HttpRequestException or IOException or TaskCanceledException);
            var delay = graphError?.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(3600, 10 * Math.Pow(2, Math.Min(upload.Attempts, 8))));
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            var message = graphError?.Message ?? (ex is InvalidDataException ? "Error de integridad del archivo temporal." : $"Error de publicación: {ex.GetType().Name}.");
            repository.Update(s =>
            {
                var item = s.SharePointUploads.Single(u => u.Id == upload.Id);
                if (item.LeaseId != lease || item.Status != "Uploading") return false;
                item.Status = retry ? "Pending" : "Failed"; item.NextAttempt = retry ? DateTimeOffset.UtcNow.Add(delay) : null;
                item.LeaseId = null; item.LeaseUntil = null; item.Error = message;
                AuditTrail.Add(s, item.User, "Publicación SharePoint", item.Account, $"{(retry ? "Reintento pendiente" : "Error")}: {item.RemotePath}. {message}");
                return true;
            });
        }
        return true;
    }

    private async Task Cleanup(SharePointUpload upload, CancellationToken cancellation)
    {
        var file = storage.Resolve(upload.LocalPath);
        if (File.Exists(file))
        {
            if (DownloadPipeline.Hash(await File.ReadAllBytesAsync(file, cancellation)) != upload.Sha256) throw new InvalidDataException("El archivo temporal cambió después de la publicación; se conservó.");
            File.Delete(file);
        }
        repository.Update(s => { var item = s.SharePointUploads.Single(u => u.Id == upload.Id); if (item.Status == "Completed") item.Cleaned = true; return true; });
    }
}

using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>Generates reports in an isolated lease workspace and publishes immutable bytes.</summary>
public sealed class OwnedReportStorage(IStorageProvider storage, OwnedContentStore content, ExecutionOwnership ownership) : IStorageProvider
{
    public string AccountDirectory(string account) => storage.AccountDirectory(account);
    public void CheckAccess() => storage.CheckAccess();
    public string Resolve(string relative) => storage.Resolve(relative);
    public string Save(Voucher document, string extension, byte[] bytes) =>
        throw new InvalidOperationException("Los documentos deben pasar por DownloadPipeline protegido.");

    public string Report(string account, int year, int month, IEnumerable<Voucher> documents)
    {
        var relativeWorkspace = Path.Combine(".staging", Validation.Identifier(ownership.JobId),
            $"{ownership.LeaseId:N}-{ownership.FencingToken}", Guid.NewGuid().ToString("N"));
        var workspace = storage.Resolve(relativeWorkspace);
        try
        {
            var isolated = new DocumentStorage(workspace);
            var report = isolated.Report(account, year, month, documents);
            var bytes = File.ReadAllBytes(isolated.Resolve(report));
            return content.Publish(bytes, "xlsx", (_, path, _) => path);
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
    }
}

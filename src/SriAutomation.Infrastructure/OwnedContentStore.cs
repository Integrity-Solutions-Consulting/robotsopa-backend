using SriAutomation.Application;
using SriAutomation.Domain;

namespace SriAutomation.Infrastructure;

/// <summary>
/// Stages bytes under one lease generation and commits only immutable,
/// content-addressed objects. An object is not visible to consumers until its
/// path is committed to state by the fenced mutation.
/// </summary>
public sealed class OwnedContentStore(IStorageProvider storage, OwnedStateRepository repository, ExecutionOwnership ownership)
{
    internal bool Uses(IStateRepository candidate) => ReferenceEquals(repository, candidate);
    public T Publish<T>(byte[] content, string extension, Func<State, string, string, T> commit)
    {
        if (extension is not ("xml" or "pdf" or "xlsx")) throw new ArgumentException("Extensión inválida.");
        if (content.Length is 0 or > 20_000_000) throw new ArgumentException("Tamaño de archivo inválido.");
        var hash = DownloadPipeline.Hash(content);
        var staging = Path.Combine(".staging", Validation.Identifier(ownership.JobId),
            $"{ownership.LeaseId:N}-{ownership.FencingToken}", Guid.NewGuid().ToString("N") + ".tmp");
        var temporary = storage.Resolve(staging);
        var relative = Path.Combine(".objects", hash[..2], hash + "." + extension);
        var target = storage.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(content); file.Flush(true); }
            repository.VerifyOwnership();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Coordinate publishers before repairing a corrupt content-addressed
            // object. Keep the corrupt bytes for investigation; never delete them.
            using var gate = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(target) && DownloadPipeline.Hash(File.ReadAllBytes(target)) != hash)
            {
                var quarantine = storage.Resolve(Path.Combine(".corrupt", hash[..2], hash + "." + Guid.NewGuid().ToString("N") + "." + extension));
                Directory.CreateDirectory(Path.GetDirectoryName(quarantine)!);
                File.Move(target, quarantine, false);
            }
            if (!File.Exists(target)) File.Move(temporary, target, false);
            return repository.Update(state => commit(state, relative, hash));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

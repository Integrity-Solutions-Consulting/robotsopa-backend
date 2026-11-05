using SriAutomation.Application;
using SriAutomation.Infrastructure;

namespace SriAutomation.Worker;

/// <summary>Builds the host processor using an execution-scoped service factory.</summary>
public static class WorkerProcessing
{
    public static JobProcessor Create(IStateRepository repository, IStorageProvider storage,
        ISriComprobanteService legacy, Func<OwnedStateRepository, OwnedContentStore, ISriComprobanteService> ownedSri,
        bool allowOwnedSri = false) => new(repository, storage, legacy, ownedSri, allowOwnedSri);
}

using Microsoft.Extensions.Diagnostics.HealthChecks;
using SriAutomation.Application;

namespace SriAutomation.Api;

public sealed class StorageHealthCheck(IStorageProvider storage, IStateRepository repository) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { storage.CheckAccess(); repository.Read(s => s.Accounts.Count); return Task.FromResult(HealthCheckResult.Healthy()); }
        catch { return Task.FromResult(HealthCheckResult.Unhealthy("Persistencia o destino no disponibles.")); }
    }
}

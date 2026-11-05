using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SriAutomation.Infrastructure;
using SriAutomation.Automation;
using SriAutomation.Application;
namespace SriAutomation.Worker;

public sealed record WorkerSettings(string StatePath);
public sealed class SriBackgroundService(JobProcessor processor, WorkerAuthentication authentication,WorkerSettings settings, IStateRepository repository, ILogger<SriBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await RunPostgres(connectionString, stoppingToken);
            return;
        }
        Directory.CreateDirectory(settings.StatePath);
        using var workerLock = new FileStream(Path.Combine(settings.StatePath, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await RunQueue(stoppingToken);
    }

    private async Task RunPostgres(string connectionString, CancellationToken stoppingToken)
    {
        var coordinator = new PostgresExecutionCoordinator(connectionString);
        var workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";
        var leaseDuration = TimeSpan.FromSeconds(30);
        var nextMaintenance = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextMaintenance)
                {
                    coordinator.FinalizeAbandonedCancellations();
                    processor.MaintainPostgresQueue();
                    nextMaintenance = DateTimeOffset.UtcNow.AddSeconds(5);
                }
                var candidates = repository.Read(s => s.Executions.Where(e => !e.CancelRequested && e.Status is "Pending" or "Running").Select(e => e.Id).ToList());
                var processed = false;
                foreach (var id in candidates)
                {
                    var ownership = coordinator.Acquire(id, workerId, leaseDuration);
                    if (ownership is null) continue;
                    processed = true;
                    using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var userCancelled = false;
                    var renewal = RenewLease(coordinator, ownership, leaseDuration, processing, () => userCancelled = true);
                    var finished = false;
                    try
                    {
                        await processor.ProcessOwned(ownership, processing.Token);
                        finished = true;
                    }
                    catch (OperationCanceledException) when (userCancelled && !stoppingToken.IsCancellationRequested)
                    {
                        // ProcessOwned rechecks the cancellation flag inside UpdateOwned.
                        await processor.ProcessOwned(ownership, CancellationToken.None);
                        finished = true;
                    }
                    finally
                    {
                        processing.Cancel();
                        await renewal;
                        if (finished && !stoppingToken.IsCancellationRequested && !coordinator.Release(ownership))
                            logger.LogWarning("No se liber? el lease de {Job}: la propiedad ya no era v?lida.", id);
                    }
                    break;
                }
                if (!processed) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error procesando la cola PostgreSQL; no se declara ?xito.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RenewLease(PostgresExecutionCoordinator coordinator, ExecutionOwnership ownership, TimeSpan duration, CancellationTokenSource processing, Action onUserCancellation)
    {
        try
        {
            while (!processing.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), processing.Token);
                if (!coordinator.Renew(ownership, duration)) throw new InvalidOperationException("Lease perdido.");
                repository.Update(s => { s.WorkerHeartbeat = DateTimeOffset.UtcNow; return true; });
                if (repository.Read(s => s.Executions.Single(e => e.Id == ownership.JobId).CancelRequested))
                {
                    onUserCancellation();
                    processing.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (processing.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "No se pudo renovar el lease; cancelando el trabajo."); processing.Cancel(); }
    }

    private async Task RunQueue(CancellationToken stoppingToken)
    {
        processor.Recover();
        logger.LogInformation("Worker activo. Consumidor exclusivo de la cola.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await authentication.Tick(stoppingToken);
            var processed = await processor.Tick(stoppingToken);
            if (!processed) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}

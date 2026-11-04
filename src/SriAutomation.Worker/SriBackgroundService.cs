using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SriAutomation.Infrastructure;
using SriAutomation.Automation;
namespace SriAutomation.Worker;

public sealed record WorkerSettings(string StatePath);
public sealed class SriBackgroundService(JobProcessor processor, WorkerAuthentication authentication,WorkerSettings settings, ILogger<SriBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(settings.StatePath);
        using var workerLock = new FileStream(Path.Combine(settings.StatePath, ".worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        processor.Recover();
        logger.LogInformation("Worker activo. Consumidor exclusivo de la cola local.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await authentication.Tick(stoppingToken);
            var processed = await processor.Tick(stoppingToken);
            if (!processed) await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}

// El pulso es independiente de la duración de la autenticación y de cada trabajo.
public sealed class WorkerHeartbeatService(SriAutomation.Application.IStateRepository repository, ILogger<WorkerHeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { repository.Update(s => { s.WorkerHeartbeat = DateTimeOffset.UtcNow; return true; }); }
            catch (Exception ex) { logger.LogError(ex, "No se pudo actualizar el heartbeat del Worker en PostgreSQL."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

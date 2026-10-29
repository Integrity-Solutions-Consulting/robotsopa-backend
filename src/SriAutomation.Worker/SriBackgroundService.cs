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

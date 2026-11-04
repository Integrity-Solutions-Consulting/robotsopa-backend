using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SriAutomation.Infrastructure;

namespace SriAutomation.Worker;

public sealed class SharePointBackgroundService(SharePointUploadProcessor processor, ILogger<SharePointBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await processor.Tick(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Error de la cola SharePoint: {Type}", ex.GetType().Name); await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        }
    }
}

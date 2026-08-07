using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Import;

public sealed class ImportWorker(
    ImportQueue queue,
    Core.Contracts.IImportProcessor processor,
    ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await queue.RecoverAsync(stoppingToken);

        await foreach (var importId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await processor.ProcessAsync(importId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Import {ImportId} failed.", importId);
            }
        }
    }
}

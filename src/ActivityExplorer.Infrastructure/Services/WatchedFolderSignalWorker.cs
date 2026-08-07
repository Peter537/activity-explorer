using ActivityExplorer.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class WatchedFolderSignalWorker(
    IWatchedFolderService service,
    ILogger<WatchedFolderSignalWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _signal = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshWatchersAsync(stoppingToken);
                var signaled = await _signal.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken);
                if (!signaled) continue;
                await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
                await service.ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Watched-folder event monitoring stopped; periodic reconciliation remains active.");
        }
        finally
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _signal.Dispose();
        }
    }

    private async Task RefreshWatchersAsync(CancellationToken cancellationToken)
    {
        var folders = await service.ListAsync(null, cancellationToken);
        var activeIds = folders.Where(x => x.Enabled && Directory.Exists(x.Path)).Select(x => x.Id).ToHashSet();
        foreach (var stale in _watchers.Keys.Where(x => !activeIds.Contains(x)).ToArray())
        {
            _watchers[stale].Dispose();
            _watchers.Remove(stale);
        }

        foreach (var folder in folders.Where(x => x.Enabled && Directory.Exists(x.Path) && !_watchers.ContainsKey(x.Id)))
        {
            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            watcher.Filters.Add("*.fit");
            watcher.Filters.Add("*.gpx");
            watcher.Filters.Add("*.tcx");
            watcher.Filters.Add("*.gz");
            watcher.Filters.Add("*.zip");
            watcher.Created += (_, _) => Signal();
            watcher.Changed += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            _watchers[folder.Id] = watcher;
        }
    }

    private void Signal()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }
}

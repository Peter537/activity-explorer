using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class WatchedFolderService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IImportQueue queue,
    AppDataPaths paths, IOwnerMutationLock? ownerMutationLock = null,
    ILogger<WatchedFolderService>? logger = null) : IWatchedFolderService
{
    private readonly IOwnerMutationLock _mutations = ownerMutationLock ?? new OwnerMutationLock();
    private static readonly string[] Supported = [".fit", ".gpx", ".tcx", ".gz", ".zip"];

    public async Task<IReadOnlyList<WatchedFolderSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.WatchedFolders.AsNoTracking().Join(db.Owners.AsNoTracking(), x => x.OwnerId, x => x.Id, (folder, owner) => new { folder, owner });
        if (ownerId.HasValue) query = query.Where(x => x.folder.OwnerId == ownerId);
        return await query.OrderBy(x => x.folder.Path)
            .Select(x => new WatchedFolderSummary(x.folder.Id, x.folder.OwnerId, x.owner.DisplayName, x.folder.Path, x.folder.Enabled, x.folder.LastScanAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> AddAsync(Guid ownerId, string path, CancellationToken cancellationToken = default)
    {
        await using var mutation = await _mutations.AcquireAsync([ownerId], cancellationToken);
        var fullPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The watched folder does not exist.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == ownerId, cancellationToken))
            throw new InvalidOperationException("Profile was not found.");
        var existing = await db.WatchedFolders.SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.Path == fullPath, cancellationToken);
        if (existing is not null) return existing.Id;
        var folder = new WatchedFolder { OwnerId = ownerId, Path = fullPath };
        db.WatchedFolders.Add(folder);
        await db.SaveChangesAsync(cancellationToken);
        return folder.Id;
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var mutation = await _mutations.AcquireAsync([], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folder = await db.WatchedFolders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (folder is null) return;
        db.WatchedFolders.Remove(folder);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        _mutations.RunAsync(async () => { await ReconcileCoreAsync(cancellationToken); return true; }, cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folders = await db.WatchedFolders.Where(x => x.Enabled).ToListAsync(cancellationToken);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Supported.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromSeconds(10)) continue;
                        // Skip staging familiar content. Admission independently hashes the staged bytes.
                        var hash = await Fingerprint.Sha256Async(file, cancellationToken);
                        var format = ImportQueue.GetWatchedFormat(file);
                        if (!await ImportQueue.IsKnownWatchedContentAsync(db, folder.OwnerId, hash, format, cancellationToken))
                            await StageAsync(folder.OwnerId, file, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger?.LogWarning("Could not stage a file from watched folder {FolderId} ({ErrorType}).", folder.Id, exception.GetType().Name);
                    }
                }
                folder.LastScanAtUtc = DateTimeOffset.UtcNow;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Could not list watched folder {FolderId} ({ErrorType}).", folder.Id, exception.GetType().Name);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task StageAsync(Guid ownerId, string file, CancellationToken cancellationToken)
    {
        var stageDirectory = ManagedStagingDirectory.Create(paths.StagingPath);
        var name = Path.GetFileName(file);
        var staged = Path.Combine(stageDirectory, name);
        var admitted = false;
        try
        {
            await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, cancellationToken);
            admitted = await queue.EnqueueWatchedAsync(ownerId, staged, name, cancellationToken);
        }
        finally
        {
            if (!admitted) ManagedStagingDirectory.DeleteOwned(paths.StagingPath, stageDirectory);
        }
    }
}

public sealed class WatchedFolderWorker(
    IWatchedFolderService service,
    ILogger<WatchedFolderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        do
        {
            try
            {
                await service.ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Watched-folder reconciliation failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

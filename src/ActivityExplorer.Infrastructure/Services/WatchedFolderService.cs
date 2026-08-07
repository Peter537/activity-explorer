using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class WatchedFolderService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IImportQueue queue,
    AppDataPaths paths) : IWatchedFolderService
{
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
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folder = await db.WatchedFolders.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (folder is null) return;
        db.WatchedFolders.Remove(folder);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var folders = await db.WatchedFolders.Where(x => x.Enabled).ToListAsync(cancellationToken);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder.Path)) continue;
            foreach (var file in Directory.EnumerateFiles(folder.Path, "*", SearchOption.TopDirectoryOnly))
            {
                if (!Supported.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(10)) continue;
                var hash = await ActivityExplorer.Infrastructure.Processing.Fingerprint.Sha256Async(file, cancellationToken);
                if (await db.SourceFiles.AsNoTracking().AnyAsync(x => x.OwnerId == folder.OwnerId && x.Sha256 == hash, cancellationToken)) continue;
                var stageDirectory = ManagedPathGuard.ResolveUnder(
                    paths.StagingPath,
                    Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(stageDirectory);
                var staged = Path.Combine(stageDirectory, Path.GetFileName(file));
                try
                {
                    File.Copy(file, staged, overwrite: false);
                    await queue.EnqueueAsync(
                        new ImportRequest(folder.OwnerId, staged, Path.GetFileName(file), SourceKind.WatchedFolder),
                        cancellationToken);
                }
                catch
                {
                    if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, recursive: true);
                    throw;
                }
            }
            folder.LastScanAtUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
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

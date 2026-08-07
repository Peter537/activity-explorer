using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ActivityExplorer.Infrastructure.Storage;

public sealed class DatabaseInitializer(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    AppDataPaths paths,
    Core.Contracts.IOriginalStore originals,
    Core.Contracts.IFileOperationCoordinator fileOperations,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await ReportUntrackedOriginalsAsync(db, cancellationToken);

        var interrupted = await db.ImportBatches
            .Where(x => x.Status == Core.Domain.ImportStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var batch in interrupted)
        {
            batch.Status = Core.Domain.ImportStatus.Interrupted;
            batch.ErrorMessage = "The application stopped before this import completed. It will resume automatically.";
            batch.CompletedAtUtc = null;
        }
        if (interrupted.Count > 0) await db.SaveChangesAsync(cancellationToken);

        await fileOperations.RecoverAsync(cancellationToken);
    }

    private async Task ReportUntrackedOriginalsAsync(ExplorerDbContext db, CancellationToken cancellationToken)
    {
        var tracked = (await db.SourceFiles.AsNoTracking().Select(x => x.StoredPath).ToListAsync(cancellationToken))
            .Select(originals.ResolveStoredPath)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var untrackedCount = Directory.EnumerateFiles(paths.OriginalsPath, "*", SearchOption.AllDirectories)
            .Count(path => !tracked.Contains(Path.GetFullPath(path)));
        if (untrackedCount > 0)
            logger.LogWarning("Found {Count} untracked managed original files. They were retained for manual review.", untrackedCount);
    }
}

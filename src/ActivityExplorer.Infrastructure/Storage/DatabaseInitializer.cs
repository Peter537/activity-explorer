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
        await EnsureSegmentProvenanceColumnsAsync(db, cancellationToken);
        await EnsureSegmentEffortMetricColumnsAsync(db, cancellationToken);
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

    internal static async Task EnsureSegmentProvenanceColumnsAsync(
        ExplorerDbContext db,
        CancellationToken cancellationToken = default)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('Segments')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        if (!existing.Contains("SourceKind"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Segments\" ADD COLUMN \"SourceKind\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        if (!existing.Contains("SourceName"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Segments\" ADD COLUMN \"SourceName\" TEXT NULL", cancellationToken);
        if (!existing.Contains("SourceFormat"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Segments\" ADD COLUMN \"SourceFormat\" TEXT NULL", cancellationToken);
    }

    internal static async Task EnsureSegmentEffortMetricColumnsAsync(
        ExplorerDbContext db,
        CancellationToken cancellationToken = default)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('SegmentEfforts')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        if (!existing.Contains("RecordedDistanceMeters"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"SegmentEfforts\" ADD COLUMN \"RecordedDistanceMeters\" REAL NULL", cancellationToken);
        if (!existing.Contains("MetricComputationVersion"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"SegmentEfforts\" ADD COLUMN \"MetricComputationVersion\" INTEGER NOT NULL DEFAULT 1", cancellationToken);
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

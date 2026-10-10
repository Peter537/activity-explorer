using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        await EnsureSegmentLinksAsync(db, cancellationToken);
        await EnsureOrganizationSchemaAsync(db, cancellationToken);
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

    internal static async Task EnsureSegmentLinksAsync(ExplorerDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SegmentLinks" (
                "ParentSegmentId" TEXT NOT NULL,
                "ChildSegmentId" TEXT NOT NULL,
                CONSTRAINT "PK_SegmentLinks" PRIMARY KEY ("ParentSegmentId", "ChildSegmentId"),
                CONSTRAINT "FK_SegmentLinks_Segments_ParentSegmentId" FOREIGN KEY ("ParentSegmentId") REFERENCES "Segments" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_SegmentLinks_Segments_ChildSegmentId" FOREIGN KEY ("ChildSegmentId") REFERENCES "Segments" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_SegmentLinks_ChildSegmentId" ON "SegmentLinks" ("ChildSegmentId");
            """, cancellationToken);
    }

    internal static async Task EnsureOrganizationSchemaAsync(ExplorerDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Tags" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "OwnerId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "NormalizedName" TEXT NOT NULL,
                FOREIGN KEY ("OwnerId") REFERENCES "Owners" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Tags_OwnerId_NormalizedName" ON "Tags" ("OwnerId", "NormalizedName");
            CREATE TABLE IF NOT EXISTS "ActivityTags" (
                "ActivityId" TEXT NOT NULL,
                "TagId" TEXT NOT NULL,
                PRIMARY KEY ("ActivityId", "TagId"),
                FOREIGN KEY ("ActivityId") REFERENCES "Activities" ("Id") ON DELETE CASCADE,
                FOREIGN KEY ("TagId") REFERENCES "Tags" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ActivityTags_TagId" ON "ActivityTags" ("TagId");
            CREATE TABLE IF NOT EXISTS "SavedSearches" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "OwnerId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "NormalizedName" TEXT NOT NULL,
                "CriteriaJson" TEXT NOT NULL,
                FOREIGN KEY ("OwnerId") REFERENCES "Owners" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SavedSearches_OwnerId_NormalizedName" ON "SavedSearches" ("OwnerId", "NormalizedName");
            """, cancellationToken);
        var hasVersion = false;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "PRAGMA table_info('Activities')";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (reader.GetString(1) == "MutationVersion") hasVersion = true;
        }
        if (!hasVersion)
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Activities\" ADD COLUMN \"MutationVersion\" INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

internal sealed class WatchedImportAdmission
{
    public Guid OwnerId { get; set; }
    public required string Sha256 { get; set; }
    public required string Format { get; set; }
    public Guid BatchId { get; set; }
    public ImportBatch Batch { get; set; } = null!;
    public bool Rejected { get; set; }
}

internal sealed class ImportBatchCheckpoint
{
    public Guid BatchId { get; set; }
    public int NextEntry { get; set; }
}

internal static class ImportLifecycleSchema
{
    internal const string Admissions = "WatchedImportAdmissions";
    internal const string Checkpoints = "ImportBatchCheckpoints";

    internal static void Configure(ModelBuilder model)
    {
        var admissions = model.Entity<WatchedImportAdmission>().ToTable(Admissions);
        admissions.HasKey(x => new { x.OwnerId, x.Sha256, x.Format });
        admissions.HasOne(x => x.Batch).WithOne().HasForeignKey<WatchedImportAdmission>(x => x.BatchId);

        var checkpoints = model.Entity<ImportBatchCheckpoint>().ToTable(Checkpoints);
        checkpoints.HasKey(x => x.BatchId);
        checkpoints.HasOne<ImportBatch>().WithOne().HasForeignKey<ImportBatchCheckpoint>(x => x.BatchId);
    }

    internal static Task EnsureAsync(ExplorerDbContext db, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "WatchedImportAdmissions" (
                "OwnerId" TEXT NOT NULL, "Sha256" TEXT NOT NULL, "Format" TEXT NOT NULL, "BatchId" TEXT NOT NULL,
                "Rejected" INTEGER NOT NULL, PRIMARY KEY ("OwnerId", "Sha256", "Format"),
                FOREIGN KEY ("BatchId") REFERENCES "ImportBatches" ("Id") ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_WatchedImportAdmissions_BatchId"
                ON "WatchedImportAdmissions" ("BatchId");
            CREATE TABLE IF NOT EXISTS "ImportBatchCheckpoints" (
                "BatchId" TEXT NOT NULL PRIMARY KEY, "NextEntry" INTEGER NOT NULL,
                FOREIGN KEY ("BatchId") REFERENCES "ImportBatches" ("Id") ON DELETE CASCADE);
            """, cancellationToken);
}

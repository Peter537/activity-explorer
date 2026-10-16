using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

internal static class ImportBatchQuery
{
    public static async Task ValidateAsync(ExplorerDbContext db, Guid? ownerId, Guid? batchId, CancellationToken cancellationToken)
    {
        if (!batchId.HasValue) return;
        if (batchId == Guid.Empty) throw new ArgumentException("Choose a valid import batch.", nameof(batchId));
        if (!ownerId.HasValue) throw new ArgumentException("Select one profile to open an import's linked results.", nameof(ownerId));
        if (!await db.ImportBatches.AnyAsync(batch => batch.Id == batchId && batch.OwnerId == ownerId, cancellationToken))
            throw new InvalidOperationException("This import no longer exists or belongs to another profile. Switch to its profile or remove the import filter.");
    }
}

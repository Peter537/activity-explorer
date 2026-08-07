using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ImportHistoryService(IDbContextFactory<ExplorerDbContext> contextFactory) : IImportHistoryService
{
    public async Task<ImportHistoryPage> ListAsync(
        Guid? ownerId, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.ImportBatches.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (ownerId.HasValue) query = query.Where(x => x.OwnerId == ownerId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(limit)
            .Select(x => new ImportBatchSummary(
                x.Id, x.OwnerId, x.Owner!.DisplayName, x.DisplayName, x.SourceKind, x.Kind, x.Status,
                x.FilesDiscovered, x.ActivitiesCreated, x.ActivitiesUpdated, x.DuplicatesSkipped + x.UnsupportedSkipped,
                x.Warnings, x.CreatedAtUtc, x.ErrorMessage ?? x.Summary))
            .ToListAsync(cancellationToken);
        return new ImportHistoryPage(items, total);
    }

    public async Task<ImportReport?> GetReportAsync(Guid importId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ImportBatches.AsNoTracking().Where(x => x.Id == importId)
            .Select(x => new ImportReport(
                x.Id, x.Status, x.ActivitiesCreated, x.ActivitiesUpdated, x.DuplicatesSkipped,
                x.UnsupportedSkipped, x.Warnings, x.ErrorMessage ?? x.Summary))
            .SingleOrDefaultAsync(cancellationToken);
    }
}

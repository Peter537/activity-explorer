using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ImportHistoryService(IDbContextFactory<ExplorerDbContext> contextFactory) : IImportHistoryService
{
    public Task<ImportHistoryPage> ListAsync(Guid? ownerId, int limit = 10, CancellationToken cancellationToken = default) =>
        SearchAsync(new(ownerId), limit, cancellationToken);

    public async Task<ImportHistoryPage> SearchAsync(
        ImportHistoryFilter filter, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        filter = ImportHistoryFiltering.Normalize(filter);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var query = db.ImportBatches.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (filter.OwnerId.HasValue) query = query.Where(x => x.OwnerId == filter.OwnerId);
        if (filter.Search is { } search) query = query.Where(x => ActivitySearchFunctions.Contains(x.DisplayName, search));
        if (filter.Statuses is { Count: > 0 })
        {
            var statuses = filter.Statuses.ToArray();
            query = query.Where(x => statuses.Contains(x.Status));
        }
        if (filter.HasWarnings.HasValue)
            query = filter.HasWarnings.Value ? query.Where(x => x.Warnings > 0) : query.Where(x => x.Warnings == 0);
        if (filter.Source.HasValue) query = query.Where(x => x.SourceKind == filter.Source);
        if (filter.Kind.HasValue) query = query.Where(x => x.Kind == filter.Kind);
        if (filter.From.HasValue || filter.To.HasValue)
        {
            var periods = await ReportingDateQuery.ResolveAsync(db, filter.OwnerId,
                new(ReportingPreset.Custom, filter.From, filter.To), DateTimeOffset.UtcNow, cancellationToken);
            query = ReportingDateQuery.ApplyImports(query, periods);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(limit)
            .Select(x => new ImportBatchSummary(
                x.Id, x.OwnerId, x.Owner!.DisplayName, x.DisplayName, x.SourceKind, x.Kind, x.Status,
                x.FilesDiscovered, x.ActivitiesCreated, x.ActivitiesUpdated, x.DuplicatesSkipped + x.UnsupportedSkipped,
                x.Warnings, x.CreatedAtUtc, x.ErrorMessage ?? x.Summary,
                db.Activities.Count(activity => activity.OwnerId == x.OwnerId && activity.SourceFiles.Any(source =>
                    source.ImportBatchId == x.Id && source.OwnerId == x.OwnerId)),
                db.Routes.Count(route => route.OwnerId == x.OwnerId && route.SourceFiles.Any(source =>
                    source.ImportBatchId == x.Id && source.OwnerId == x.OwnerId))))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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

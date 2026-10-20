using System.Linq.Expressions;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentReadService(IDbContextFactory<ExplorerDbContext> contextFactory) : ISegmentReadService
{
    public async Task<IReadOnlyList<SegmentSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = db.Segments.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (ownerId.HasValue) source = source.Where(x => x.OwnerId == ownerId);
        var segments = await source.OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var ids = segments.Select(x => x.Id).ToArray();
        var efforts = await db.SegmentEfforts.AsNoTracking().Where(x => ids.Contains(x.SegmentId))
            .GroupBy(x => x.SegmentId)
            .Select(x => new { Id = x.Key, Count = x.Count(), Best = (double?)x.Min(e => e.ElapsedSeconds) })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        return segments.Select(x =>
        {
            efforts.TryGetValue(x.Id, out var effort);
            return new SegmentSummary(x.Id, x.OwnerId, x.Owner?.DisplayName ?? "Unknown profile", x.Name, x.Sport,
                x.DistanceMeters, x.ToleranceMeters, effort?.Count ?? 0, effort?.Best, x.AverageGradePercent,
                x.ElevationGainMeters, x.ElevationLossMeters, x.SourceKind, x.SourceName, x.SourceFormat);
        }).ToArray();
    }

}

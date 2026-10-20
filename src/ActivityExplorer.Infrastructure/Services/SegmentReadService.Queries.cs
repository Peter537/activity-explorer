using System.Linq.Expressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentReadService
{
    private static readonly Expression<Func<SegmentEffort, SegmentEffortSummary>> EffortProjection = effort => new(
        effort.Id, effort.ActivityId, effort.SegmentId, effort.Segment!.Name, effort.ElapsedSeconds, effort.Rank,
        effort.StartTimeUtc, effort.StartPointIndex, effort.EndPointIndex, effort.MovingSeconds,
        effort.AverageSpeedMetersPerSecond, effort.MaxSpeedMetersPerSecond, effort.AverageHeartRate, effort.MaxHeartRate,
        effort.AverageCadence, effort.MaxCadence, effort.AveragePowerWatts, effort.MaxPowerWatts,
        effort.AverageTemperatureCelsius, effort.AverageRespirationRate, effort.RecordedDistanceMeters,
        effort.ElevationGainMeters, effort.ElevationLossMeters, effort.AverageGradePercent, effort.CoveragePercent,
        effort.MetricComputationVersion == SegmentEffortMetricVersions.Current);

    private static IQueryable<SegmentEffort> Efforts(ExplorerDbContext db, Guid ownerId, Guid segmentId) =>
        db.SegmentEfforts.AsNoTracking().Where(effort => effort.OwnerId == ownerId && effort.SegmentId == segmentId &&
            effort.Segment!.OwnerId == ownerId && effort.Activity!.OwnerId == ownerId && effort.Activity.Sport == effort.Segment.Sport);

    private static IQueryable<SegmentSummary> Summaries(IQueryable<Segment> source, ExplorerDbContext db) => source.Select(segment =>
        new SegmentSummary(segment.Id, segment.OwnerId, segment.Owner!.DisplayName, segment.Name, segment.Sport,
            segment.DistanceMeters, segment.ToleranceMeters,
            db.SegmentEfforts.Count(effort => effort.SegmentId == segment.Id && effort.OwnerId == segment.OwnerId && effort.Activity!.OwnerId == segment.OwnerId && effort.Activity.Sport == segment.Sport),
            db.SegmentEfforts.Where(effort => effort.SegmentId == segment.Id && effort.OwnerId == segment.OwnerId && effort.Activity!.OwnerId == segment.OwnerId && effort.Activity.Sport == segment.Sport).Min(effort => (double?)effort.ElapsedSeconds),
            segment.AverageGradePercent, segment.ElevationGainMeters, segment.ElevationLossMeters,
            segment.SourceKind, segment.SourceName, segment.SourceFormat));

    public async Task<PagedResult<SegmentSummary>> SearchAsync(Guid ownerId, string? search, SportKind? sport, string sort, ReadPage page, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        if (sport.HasValue && !Enum.IsDefined(sport.Value)) throw new ArgumentException("Choose a valid sport.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var query = db.Segments.AsNoTracking().Where(segment => segment.OwnerId == ownerId);
        if (sport.HasValue) query = query.Where(segment => segment.Sport == sport);
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(segment => ActivitySearchFunctions.Contains(segment.Name, search.Trim()));
        var count = await query.CountAsync(cancellationToken);
        var ordered = sort switch
        {
            "name-asc" => query.OrderBy(segment => segment.Name),
            "name-desc" => query.OrderByDescending(segment => segment.Name),
            "distance-asc" => query.OrderBy(segment => segment.DistanceMeters),
            "distance-desc" => query.OrderByDescending(segment => segment.DistanceMeters),
            _ => throw new ArgumentException("Choose name-asc, name-desc, distance-asc, or distance-desc.")
        };
        var items = await Summaries(ordered.ThenBy(segment => segment.Id).Skip(offset).Take(page.PageSize), db).ToArrayAsync(cancellationToken);
        return new(items, count, page.Page, page.PageSize);
    }

    public async Task<SegmentReadDetail?> GetAsync(Guid ownerId, Guid segmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var summary = await Summaries(db.Segments.AsNoTracking().Where(segment => segment.OwnerId == ownerId && segment.Id == segmentId), db).SingleOrDefaultAsync(cancellationToken);
        if (summary is null) return null;
        return new(summary,
            await db.SegmentLinks.CountAsync(link => link.ChildSegmentId == segmentId && link.ParentSegment!.OwnerId == ownerId, cancellationToken),
            await db.SegmentLinks.CountAsync(link => link.ParentSegmentId == segmentId && link.ChildSegment!.OwnerId == ownerId, cancellationToken),
            !await Efforts(db, ownerId, segmentId).AnyAsync(effort => effort.MetricComputationVersion != SegmentEffortMetricVersions.Current, cancellationToken));
    }

    public async Task<PagedResult<SegmentEffortSummary>?> GetEffortsAsync(Guid ownerId, Guid segmentId, SegmentEffortFilter filter, ReadPage page, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        SegmentEffortFinding.Validate(filter);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        if (!await db.Segments.AnyAsync(segment => segment.OwnerId == ownerId && segment.Id == segmentId, cancellationToken)) return null;
        var periods = await ReportingDateQuery.ResolveAsync(db, ownerId, new(ReportingPreset.Custom, filter.From, filter.To), DateTimeOffset.UtcNow, cancellationToken);
        var period = periods.Single();
        var query = Efforts(db, ownerId, segmentId);
        if (period.FromUtc.HasValue) query = query.Where(effort => effort.StartTimeUtc >= period.FromUtc.Value);
        if (period.ToUtc.HasValue) query = query.Where(effort => effort.StartTimeUtc < period.ToUtc.Value);
        if (filter.MinimumElapsedSeconds.HasValue) query = query.Where(effort => effort.ElapsedSeconds >= filter.MinimumElapsedSeconds);
        if (filter.MaximumElapsedSeconds.HasValue) query = query.Where(effort => effort.ElapsedSeconds <= filter.MaximumElapsedSeconds);
        var count = await query.CountAsync(cancellationToken);
        var ordered = filter.Sort switch
        {
            "date" => query.OrderByDescending(effort => effort.StartTimeUtc),
            "elapsed" => query.OrderBy(effort => effort.ElapsedSeconds),
            "speed" => query.OrderByDescending(effort => effort.AverageSpeedMetersPerSecond),
            "heart-rate" => query.OrderByDescending(effort => effort.AverageHeartRate),
            "power" => query.OrderByDescending(effort => effort.AveragePowerWatts),
            _ => query.OrderBy(effort => effort.Rank)
        };
        var items = await ordered.ThenBy(effort => effort.StartTimeUtc).ThenBy(effort => effort.ActivityId)
            .ThenBy(effort => effort.StartPointIndex).ThenBy(effort => effort.Id)
            .Skip(offset).Take(page.PageSize).Select(EffortProjection).ToArrayAsync(cancellationToken);
        return new(items, count, page.Page, page.PageSize);
    }
}

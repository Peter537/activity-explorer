using System.Security.Cryptography;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentService
{
    public async Task<IReadOnlyList<SegmentRelationship>> ListRelationshipsAsync(
        Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SegmentLinks.AsNoTracking()
            .Where(link => !ownerId.HasValue || link.ParentSegment!.OwnerId == ownerId)
            .OrderBy(link => link.ParentSegmentId).ThenBy(link => link.ChildSegmentId)
            .Select(link => new SegmentRelationship(link.ParentSegmentId, link.ChildSegmentId))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<Guid> CreateSubsegmentAsync(CreateSubsegmentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateNameAndTolerance(request.Name, request.ToleranceMeters, nameof(request));
        await using var ownerLock = await AcquireParentLockAsync(request.ParentSegmentId, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var parent = await FindSegmentAsync(db, request.ParentSegmentId, cancellationToken);
        var parentPoints = GeometryCodec.FromWkb(parent.GeometryWkb);
        if (request.StartPointIndex < 0 || request.EndPointIndex >= parentPoints.Count ||
            request.EndPointIndex <= request.StartPointIndex ||
            request.StartPointIndex == 0 && request.EndPointIndex == parentPoints.Count - 1)
            throw new ArgumentException("Select a smaller portion with at least two GPS points.", nameof(request));

        var points = parentPoints.Skip(request.StartPointIndex).Take(request.EndPointIndex - request.StartPointIndex + 1).ToArray();
        var geometry = GeometryCodec.ToWkb(points)!;
        if (request.ReviewedGeometrySha256 is { } digest && digest != Convert.ToHexString(SHA256.HashData(geometry)))
            throw new InvalidOperationException("The path changed after review. Prepare the change again.");
        var metrics = new TrackPathAnalysis(points).Slice(0, points.Length - 1);
        var bounds = GeometryCodec.Bounds(points);
        var child = new Segment
        {
            OwnerId = parent.OwnerId,
            Sport = parent.Sport,
            Name = request.Name.Trim(),
            SourceKind = SegmentSourceKind.Segment,
            SourceName = parent.Name,
            ToleranceMeters = request.ToleranceMeters,
            GeometryWkb = geometry,
            DistanceMeters = metrics.DistanceMeters,
            ElevationGainMeters = metrics.ElevationGainMeters,
            ElevationLossMeters = metrics.ElevationLossMeters,
            AverageGradePercent = metrics.AverageGradePercent,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        if ((await FindPlacementsAsync(parentPoints, child, cancellationToken)).Count == 0)
            throw new InvalidOperationException("The selection must follow a smaller, continuous portion of this segment.");

        db.SegmentLinks.Add(new SegmentLink { ParentSegmentId = parent.Id, ChildSegmentId = child.Id });
        await SaveNewSegmentAsync(db, child, points, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return child.Id;
    }

    public async Task<SegmentAttachmentPreview> PreviewAttachmentAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ValidateAttachmentAsync(db, parentId, childId, cancellationToken);
    }

    public async Task AttachAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await AcquireParentLockAsync(parentId, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ValidateAttachmentAsync(db, parentId, childId, cancellationToken);
        db.SegmentLinks.Add(new SegmentLink { ParentSegmentId = parentId, ChildSegmentId = childId });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DetachAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await AcquireParentLockAsync(parentId, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.SegmentLinks.SingleOrDefaultAsync(item => item.ParentSegmentId == parentId && item.ChildSegmentId == childId, cancellationToken);
        if (link is null) return;
        db.SegmentLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<IAsyncDisposable> AcquireParentLockAsync(Guid parentId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ownerId = await db.Segments.Where(segment => segment.Id == parentId)
            .Select(segment => (Guid?)segment.OwnerId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("The parent segment was not found.");
        return await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
    }

    private async Task<SegmentAttachmentPreview> ValidateAttachmentAsync(
        ExplorerDbContext db, Guid parentId, Guid childId, CancellationToken cancellationToken)
    {
        if (parentId == childId) throw new InvalidOperationException("A segment cannot contain itself.");
        var parent = await FindSegmentAsync(db, parentId, cancellationToken);
        var child = await FindSegmentAsync(db, childId, cancellationToken);
        if (parent.OwnerId != child.OwnerId || parent.Sport != child.Sport)
            throw new InvalidOperationException("Choose a segment from the same profile and sport.");
        var links = await db.SegmentLinks.AsNoTracking().Where(link => link.ParentSegment!.OwnerId == parent.OwnerId)
            .Select(link => new SegmentRelationship(link.ParentSegmentId, link.ChildSegmentId)).ToListAsync(cancellationToken);
        if (links.Any(link => link.ParentSegmentId == parentId && link.ChildSegmentId == childId))
            throw new InvalidOperationException("This sub-segment is already attached.");

        var descendants = links.ToLookup(link => link.ParentSegmentId, link => link.ChildSegmentId);
        var pending = new Stack<Guid>();
        var visited = new HashSet<Guid>();
        pending.Push(childId);
        while (pending.TryPop(out var current))
        {
            if (current == parentId) throw new InvalidOperationException("This relationship would create a cycle.");
            if (!visited.Add(current)) continue;
            foreach (var descendant in descendants[current]) pending.Push(descendant);
        }

        var placements = await FindPlacementsAsync(GeometryCodec.FromWkb(parent.GeometryWkb), child, cancellationToken);
        if (placements.Count == 0)
            throw new InvalidOperationException("This segment does not follow a smaller, continuous portion of the parent in the same direction.");
        var summary = (await SummarizeAsync(db, [child], cancellationToken))[child.Id];
        return new SegmentAttachmentPreview(summary, placements);
    }

    private static async Task<Segment> FindSegmentAsync(ExplorerDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.Segments.AsNoTracking().Include(segment => segment.Owner)
            .SingleOrDefaultAsync(segment => segment.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("The segment was not found. Return to the segment list and try again.");

    private async Task<IReadOnlyList<SegmentPlacement>> FindPlacementsAsync(
        IReadOnlyList<TrackPoint> parentPoints, Segment child, CancellationToken cancellationToken)
    {
        var childPoints = GeometryCodec.FromWkb(child.GeometryWkb);
        var matches = await matcher.MatchAsync(parentPoints, childPoints, child.ToleranceMeters, cancellationToken);
        var analysis = new TrackPathAnalysis(parentPoints);
        return matches.Where(match => match.StartIndex > 0 || match.EndIndex < parentPoints.Count - 1)
            .Select(match => new SegmentPlacement(match.StartIndex, match.EndIndex,
                analysis.DistanceAt(match.StartIndex), analysis.DistanceAt(match.EndIndex)))
            .Where(placement => placement.EndDistanceMeters > placement.StartDistanceMeters).ToArray();
    }

    private sealed record RelatedChild(Segment Definition, SegmentSummary Summary, SegmentEffortSummary[] Efforts);

    private static async Task<(SegmentSummary[] Parents, RelatedChild[] Children)> ReadRelatedSegmentsAsync(
        ExplorerDbContext db, Guid segmentId,
        SegmentEffortSummary? selected, CancellationToken cancellationToken)
    {
        var links = await db.SegmentLinks.AsNoTracking()
            .Where(link => link.ParentSegmentId == segmentId || link.ChildSegmentId == segmentId)
            .Select(link => new SegmentRelationship(link.ParentSegmentId, link.ChildSegmentId)).ToListAsync(cancellationToken);
        if (links.Count == 0) return ([], []);
        var childIds = links.Where(link => link.ParentSegmentId == segmentId).Select(link => link.ChildSegmentId).ToArray();
        var parentIds = links.Where(link => link.ChildSegmentId == segmentId).Select(link => link.ParentSegmentId).ToArray();
        var relatedIds = childIds.Concat(parentIds).Distinct().ToArray();
        var related = await db.Segments.AsNoTracking().Include(value => value.Owner)
            .Where(value => relatedIds.Contains(value.Id)).ToListAsync(cancellationToken);
        var summaries = await SummarizeAsync(db, related, cancellationToken);
        var efforts = selected is null ? [] : await ProjectionVersions.CurrentEfforts(db)
            .Where(effort => childIds.Contains(effort.SegmentId) && effort.ActivityId == selected.ActivityId &&
                effort.StartPointIndex >= selected.StartPointIndex && effort.EndPointIndex <= selected.EndPointIndex)
            .OrderBy(effort => effort.StartPointIndex).ThenBy(effort => effort.EndPointIndex).ThenBy(effort => effort.Id)
            .ToListAsync(cancellationToken);
        var effortsBySegment = efforts.ToLookup(effort => effort.SegmentId);
        return (parentIds.Select(id => summaries[id]).OrderBy(parent => parent.Name).ThenBy(parent => parent.Id).ToArray(),
            related.Where(child => childIds.Contains(child.Id)).Select(child => new RelatedChild(child, summaries[child.Id],
                effortsBySegment[child.Id].Select(effort => ToEffortSummary(effort, child.Name)).ToArray())).ToArray());
    }

    private async Task<SubsegmentDetail[]> BuildSubsegmentDetailsAsync(
        RelatedChild[] related, IReadOnlyList<TrackPoint> definitionPoints, CancellationToken cancellationToken)
    {
        var children = new List<SubsegmentDetail>();
        foreach (var child in related)
            children.Add(new SubsegmentDetail(child.Summary,
                await FindPlacementsAsync(definitionPoints, child.Definition, cancellationToken), child.Efforts));
        return children.OrderBy(child => child.Placements.Count > 0 ? child.Placements[0].StartDistanceMeters : double.MaxValue)
            .ThenBy(child => child.Summary.Name).ThenBy(child => child.Summary.Id).ToArray();
    }

    private static async Task<Dictionary<Guid, SegmentSummary>> SummarizeAsync(
        ExplorerDbContext db, IReadOnlyList<Segment> segments, CancellationToken cancellationToken)
    {
        var ids = segments.Select(segment => segment.Id).ToArray();
        var totals = await ProjectionVersions.CurrentEfforts(db).Where(effort => ids.Contains(effort.SegmentId))
            .GroupBy(effort => effort.SegmentId)
            .Select(group => new { Id = group.Key, Count = group.Count(), Best = (double?)group.Min(effort => effort.ElapsedSeconds) })
            .ToDictionaryAsync(group => group.Id, cancellationToken);
        var pending = await ProjectionVersions.PendingSegments(db).Where(segment => ids.Contains(segment.Id))
            .Select(segment => segment.Id).ToArrayAsync(cancellationToken);
        return segments.ToDictionary(segment => segment.Id, segment =>
        {
            totals.TryGetValue(segment.Id, out var total);
            return new SegmentSummary(segment.Id, segment.OwnerId, segment.Owner?.DisplayName ?? "Unknown profile",
                segment.Name, segment.Sport, segment.DistanceMeters, segment.ToleranceMeters, total?.Count ?? 0, total?.Best,
                segment.AverageGradePercent, segment.ElevationGainMeters, segment.ElevationLossMeters,
                segment.SourceKind, segment.SourceName, segment.SourceFormat)
            { EffortsPending = pending.Contains(segment.Id) };
        });
    }

    private static SegmentEffortSummary ToEffortSummary(SegmentEffort effort, string name) => new(
        effort.Id, effort.ActivityId, effort.SegmentId, name, effort.ElapsedSeconds, effort.Rank, effort.StartTimeUtc,
        effort.StartPointIndex, effort.EndPointIndex, effort.MovingSeconds, effort.AverageSpeedMetersPerSecond,
        effort.MaxSpeedMetersPerSecond, effort.AverageHeartRate, effort.MaxHeartRate, effort.AverageCadence,
        effort.MaxCadence, effort.AveragePowerWatts, effort.MaxPowerWatts, effort.AverageTemperatureCelsius,
        effort.AverageRespirationRate, effort.RecordedDistanceMeters, effort.ElevationGainMeters, effort.ElevationLossMeters,
        effort.AverageGradePercent, effort.CoveragePercent, effort.MetricComputationVersion >= SegmentEffortMetricVersions.Current);
}

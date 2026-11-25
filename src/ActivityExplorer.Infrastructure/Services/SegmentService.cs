using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    ISegmentMatcher matcher,
    IOwnerMutationLock ownerMutationLock) : ISegmentService
{
    public Task<IReadOnlyList<SegmentSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default) => new SegmentReadService(contextFactory).ListAsync(ownerId, cancellationToken);

    public async Task<SegmentDetail?> GetAsync(Guid id, Guid? effortId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        return await GetDetailAsync(db, id, effortId, includeSelectedStream: true, cancellationToken, snapshot);
    }

    private async Task<SegmentDetail?> GetDetailAsync(
        ExplorerDbContext db, Guid id, Guid? effortId, bool includeSelectedStream, CancellationToken cancellationToken,
        ReadSnapshot? snapshot = null)
    {
        var segment = await db.Segments.AsNoTracking().Include(x => x.Owner).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (segment is null) return null;
        var effortsPending = await ProjectionVersions.PendingSegments(db).AnyAsync(value => value.Id == id, cancellationToken);
        var efforts = await ProjectionVersions.CurrentEfforts(db)
            .Where(x => x.SegmentId == id)
            .OrderBy(x => x.ElapsedSeconds)
            .ThenBy(x => x.StartTimeUtc)
            .ThenBy(x => x.ActivityId)
            .ThenBy(x => x.StartPointIndex)
            .Select(x => new SegmentEffortSummary(
                x.Id, x.ActivityId, x.SegmentId, segment.Name, x.ElapsedSeconds, x.Rank, x.StartTimeUtc, x.StartPointIndex, x.EndPointIndex,
                x.MovingSeconds, x.AverageSpeedMetersPerSecond, x.MaxSpeedMetersPerSecond,
                x.AverageHeartRate, x.MaxHeartRate, x.AverageCadence, x.MaxCadence, x.AveragePowerWatts, x.MaxPowerWatts,
                x.AverageTemperatureCelsius, x.AverageRespirationRate, x.RecordedDistanceMeters,
                x.ElevationGainMeters, x.ElevationLossMeters, x.AverageGradePercent, x.CoveragePercent,
                x.MetricComputationVersion >= SegmentEffortMetricVersions.Current))
            .ToListAsync(cancellationToken);
        var selected = effortId.HasValue ? efforts.FirstOrDefault(x => x.Id == effortId) : efforts.FirstOrDefault();
        var payload = includeSelectedStream && selected is not null
            ? await db.ActivityStreams.AsNoTracking().Where(x => x.ActivityId == selected.ActivityId)
                .Select(x => x.CompressedPayload).SingleOrDefaultAsync(cancellationToken)
            : null;
        var related = await ReadRelatedSegmentsAsync(db, segment.Id, selected, cancellationToken);
        if (snapshot is not null) await snapshot.CommitAsync(cancellationToken);

        IReadOnlyList<TrackPoint> selectedPoints = [];
        if (payload is not null && selected is not null)
        {
            var all = await TrackCodec.DecodeAsync(payload, cancellationToken);
            if (selected.StartPointIndex >= 0 && selected.EndPointIndex < all.Count && selected.StartPointIndex <= selected.EndPointIndex)
            {
                var slice = all.Skip(selected.StartPointIndex).Take(selected.EndPointIndex - selected.StartPointIndex + 1).ToArray();
                var analysis = new TrackPathAnalysis(slice);
                selectedPoints = slice.Select((point, index) => point with { DistanceMeters = analysis.DistanceAt(index) }).ToArray();
            }
        }
        var definitionPoints = GeometryCodec.FromWkb(segment.GeometryWkb);
        var definitionAnalysis = new TrackPathAnalysis(definitionPoints);
        var positionedDefinitionPoints = definitionPoints
            .Select((point, index) => point with { DistanceMeters = definitionAnalysis.DistanceAt(index) })
            .ToArray();
        var children = await BuildSubsegmentDetailsAsync(related.Children, definitionPoints, cancellationToken);
        return new SegmentDetail(
            new SegmentSummary(segment.Id, segment.OwnerId, segment.Owner?.DisplayName ?? "Unknown profile", segment.Name,
                segment.Sport, segment.DistanceMeters, segment.ToleranceMeters, efforts.Count,
                efforts.Count == 0 ? null : efforts.Min(x => x.ElapsedSeconds), segment.AverageGradePercent,
                segment.ElevationGainMeters, segment.ElevationLossMeters, segment.SourceKind, segment.SourceName, segment.SourceFormat)
            { EffortsPending = effortsPending },
            positionedDefinitionPoints,
            efforts, selected?.Id, selectedPoints, related.Parents, children, segment.Owner?.TimeZoneId);
    }

    public async Task<Guid> CreateFromActivityAsync(CreateSegmentRequest request, CancellationToken cancellationToken = default)
    {
        await using var mutation = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        ValidateNameAndTolerance(request.Name, request.ToleranceMeters, nameof(request));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.Activities.AsNoTracking().Include(x => x.Stream)
            .SingleOrDefaultAsync(x => x.Id == request.ActivityId && x.OwnerId == request.OwnerId, cancellationToken)
            ?? throw new InvalidOperationException("Activity was not found for this profile.");
        if (activity.Stream is null) throw new InvalidOperationException("The activity has no detailed track stream.");
        var allPoints = TrackCodec.Decode(activity.Stream.CompressedPayload);
        if (request.StartPointIndex < 0 || request.EndPointIndex >= allPoints.Count || request.EndPointIndex - request.StartPointIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "Select at least two valid track points.");
        IEnumerable<TrackPoint> selected = allPoints.Skip(request.StartPointIndex).Take(request.EndPointIndex - request.StartPointIndex + 1);
        if (request.ReverseDirection) selected = selected.Reverse();
        var points = selected
            .Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (points.Length < 2) throw new InvalidOperationException("The selected portion has insufficient GPS data.");

        var bounds = GeometryCodec.Bounds(points);
        var metrics = new TrackPathAnalysis(points).Slice(0, points.Length - 1);
        var segment = new Segment
        {
            OwnerId = request.OwnerId,
            SourceActivityId = activity.Id,
            SourceKind = SegmentSourceKind.Activity,
            SourceName = TrimProvenance(activity.Title, 260),
            Sport = activity.Sport,
            Name = request.Name.Trim(),
            DistanceMeters = metrics.DistanceMeters,
            ElevationGainMeters = metrics.ElevationGainMeters,
            ElevationLossMeters = metrics.ElevationLossMeters,
            AverageGradePercent = metrics.AverageGradePercent,
            ToleranceMeters = request.ToleranceMeters,
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        await SaveNewSegmentAsync(db, segment, points, cancellationToken);
        return segment.Id;
    }

    public async Task<Guid> CreateAsync(CreateSegmentPathRequest request, CancellationToken cancellationToken = default)
    {
        await using var mutation = await ownerMutationLock.AcquireAsync([request.OwnerId], cancellationToken);
        ValidateNameAndTolerance(request.Name, request.ToleranceMeters, nameof(request));
        ValidateProvenance(request.SourceKind, request.SourceName, request.SourceFormat, nameof(request));
        var points = ValidatePoints(request.Points, nameof(request));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");
        if (request.SourceActivityId.HasValue && !await db.Activities.AsNoTracking()
                .AnyAsync(x => x.Id == request.SourceActivityId && x.OwnerId == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The source activity was not found for this profile.");
        var bounds = GeometryCodec.Bounds(points);
        var metrics = new TrackPathAnalysis(points).Slice(0, points.Length - 1);
        var segment = new Segment
        {
            OwnerId = request.OwnerId,
            SourceActivityId = request.SourceActivityId,
            SourceKind = request.SourceKind,
            SourceName = TrimProvenance(request.SourceName, 260),
            SourceFormat = TrimProvenance(request.SourceFormat, 32)?.ToUpperInvariant(),
            Sport = request.Sport,
            Name = request.Name.Trim(),
            DistanceMeters = metrics.DistanceMeters,
            ElevationGainMeters = metrics.ElevationGainMeters,
            ElevationLossMeters = metrics.ElevationLossMeters,
            AverageGradePercent = metrics.AverageGradePercent,
            ToleranceMeters = request.ToleranceMeters,
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        await SaveNewSegmentAsync(db, segment, points, cancellationToken);
        return segment.Id;
    }

    public async Task RecomputeAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Segment? segment;
        long activityGeneration;
        long definitionGeneration;
        await using (var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken))
        {
            segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == segmentId, cancellationToken);
            if (segment is null) return;
            activityGeneration = await ProjectionVersions.ReadActivityGenerationAsync(db, segment.OwnerId, cancellationToken);
            definitionGeneration = await ProjectionVersions.ReadDefinitionGenerationAsync(db, segmentId, cancellationToken);
        }
        var segmentPoints = GeometryCodec.FromWkb(segment.GeometryWkb);
        var generated = await GenerateEffortsAsync(db, segment, segmentPoints, cancellationToken);

        await using var mutation = await ownerMutationLock.AcquireAsync([segment.OwnerId], cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (activityGeneration != await ProjectionVersions.ReadActivityGenerationAsync(db, segment.OwnerId, cancellationToken) ||
            definitionGeneration != await ProjectionVersions.ReadDefinitionGenerationAsync(db, segmentId, cancellationToken) ||
            !await db.Segments.AnyAsync(value => value.Id == segmentId && value.OwnerId == segment.OwnerId, cancellationToken)) return;

        var existing = await db.SegmentEfforts.Where(x => x.SegmentId == segmentId).ToListAsync(cancellationToken);
        var existingByKey = existing.ToDictionary(x => (x.ActivityId, x.StartPointIndex));
        foreach (var candidate in generated)
        {
            if (existingByKey.Remove((candidate.ActivityId, candidate.StartPointIndex), out var current))
                CopyEffort(candidate, current);
            else
                db.SegmentEfforts.Add(candidate);
        }
        db.SegmentEfforts.RemoveRange(existingByKey.Values);
        await ProjectionVersions.PublishSegmentAsync(db, segmentId, activityGeneration, definitionGeneration, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveNewSegmentAsync(ExplorerDbContext db, Segment segment, IReadOnlyList<TrackPoint> points,
        CancellationToken cancellationToken)
    {
        var generation = await ProjectionVersions.ReadActivityGenerationAsync(db, segment.OwnerId, cancellationToken);
        var generated = await GenerateEffortsAsync(db, segment, points, cancellationToken);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var isCurrent = generation == await ProjectionVersions.ReadActivityGenerationAsync(db, segment.OwnerId, cancellationToken);
        db.Segments.Add(segment);
        if (isCurrent) db.SegmentEfforts.AddRange(generated);
        await db.SaveChangesAsync(cancellationToken);
        if (isCurrent)
        {
            var definitionGeneration = await ProjectionVersions.ReadDefinitionGenerationAsync(db, segment.Id, cancellationToken);
            await ProjectionVersions.PublishSegmentAsync(db, segment.Id, generation, definitionGeneration, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<SegmentEffort>> GenerateEffortsAsync(
        ExplorerDbContext db,
        Segment segment,
        IReadOnlyList<TrackPoint> segmentPoints,
        CancellationToken cancellationToken)
    {
        var candidates = db.Activities.AsNoTracking().Include(x => x.Stream)
            .Where(x => x.OwnerId == segment.OwnerId && x.Sport == segment.Sport && x.HasGps);
        var activities = await ApplyCandidateBounds(candidates, segment).ToListAsync(cancellationToken);

        var generated = new List<SegmentEffort>();
        foreach (var activity in activities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activity.Stream is null) continue;
            var points = TrackCodec.Decode(activity.Stream.CompressedPayload);
            var matches = await matcher.MatchAsync(points, segmentPoints, segment.ToleranceMeters, cancellationToken);
            foreach (var match in matches)
            {
                var slice = points.Skip(match.StartIndex).Take(match.EndIndex - match.StartIndex + 1).ToArray();
                if (HasGpsGap(slice)) continue;
                var firstTime = slice.FirstOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
                var lastTime = slice.LastOrDefault(x => x.Timestamp.HasValue)?.Timestamp;
                var elapsed = firstTime.HasValue && lastTime.HasValue
                    ? (lastTime.Value - firstTime.Value).TotalSeconds
                    : activity.ElapsedTimeSeconds * (match.EndIndex - match.StartIndex) / Math.Max(points.Count - 1d, 1d);
                if (elapsed <= 0) continue;
                var effortMetrics = SegmentEffortMetricCalculator.Calculate(segment.DistanceMeters, slice, elapsed);
                generated.Add(new SegmentEffort
                {
                    OwnerId = segment.OwnerId,
                    SegmentId = segment.Id,
                    ActivityId = activity.Id,
                    StartPointIndex = match.StartIndex,
                    EndPointIndex = match.EndIndex,
                    StartTimeUtc = firstTime ?? activity.StartTimeUtc,
                    ElapsedSeconds = elapsed,
                    MovingSeconds = EstimateMovingSeconds(slice, elapsed),
                    AverageHeartRate = effortMetrics.AverageHeartRate,
                    AverageCadence = effortMetrics.AverageCadence,
                    AveragePowerWatts = effortMetrics.AveragePowerWatts,
                    RecordedDistanceMeters = effortMetrics.RecordedDistanceMeters,
                    ElevationGainMeters = effortMetrics.ElevationGainMeters,
                    ElevationLossMeters = effortMetrics.ElevationLossMeters,
                    AverageGradePercent = effortMetrics.AverageGradePercent,
                    AverageSpeedMetersPerSecond = effortMetrics.AverageSpeedMetersPerSecond,
                    MaxSpeedMetersPerSecond = effortMetrics.MaxSpeedMetersPerSecond,
                    MaxHeartRate = effortMetrics.MaxHeartRate,
                    MaxCadence = effortMetrics.MaxCadence,
                    MaxPowerWatts = effortMetrics.MaxPowerWatts,
                    AverageTemperatureCelsius = effortMetrics.AverageTemperatureCelsius,
                    AverageRespirationRate = effortMetrics.AverageRespirationRate,
                    CoveragePercent = match.CoveragePercent,
                    MetricComputationVersion = SegmentEffortMetricVersions.Current
                });
            }
        }

        var ranked = generated.OrderBy(x => x.ElapsedSeconds)
            .ThenBy(x => x.StartTimeUtc)
            .ThenBy(x => x.ActivityId)
            .ThenBy(x => x.StartPointIndex)
            .ToArray();
        for (var index = 0; index < ranked.Length; index++) ranked[index].Rank = index + 1;
        return ranked;
    }

    private static IQueryable<Activity> ApplyCandidateBounds(IQueryable<Activity> candidates, Segment segment)
    {
        var latitudePadding = Math.Max(segment.ToleranceMeters, 30) / 111_000d;
        var south = segment.MinLatitude - latitudePadding;
        var north = segment.MaxLatitude + latitudePadding;
        candidates = candidates.Where(x => x.MaxLatitude >= south && x.MinLatitude <= north &&
            x.MinLongitude.HasValue && x.MaxLongitude.HasValue);

        // Use the closest latitude to a pole within tolerance to avoid pruning possible matches.
        var furthestFromEquator = Math.Max(Math.Abs(south), Math.Abs(north));
        var longitudePadding = furthestFromEquator >= 90d ? 180d
            : Math.Min(180d, latitudePadding / Math.Cos(furthestFromEquator * Math.PI / 180d));
        var width = segment.MaxLongitude - segment.MinLongitude;
        if (width < 0d) width += 360d;
        if (width + 2d * longitudePadding >= 360d) return candidates;

        var west = segment.MinLongitude - longitudePadding;
        var east = segment.MaxLongitude + longitudePadding;
        if (west <= -180d) west += 360d;
        if (east >= 180d) east -= 360d;

        // A stored interval with MinLongitude > MaxLongitude covers both sides of the antimeridian.
        return west <= east
            ? candidates.Where(x =>
                x.MinLongitude <= x.MaxLongitude && x.MaxLongitude >= west && x.MinLongitude <= east ||
                x.MinLongitude > x.MaxLongitude && (x.MinLongitude <= east || x.MaxLongitude >= west))
            : candidates.Where(x => x.MinLongitude > x.MaxLongitude || x.MaxLongitude >= west || x.MinLongitude <= east);
    }

    public static void ValidateNameAndTolerance(string name, double toleranceMeters, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", parameterName);
        if (name.Trim().Length > 240) throw new ArgumentException("Name cannot exceed 240 characters.", parameterName);
        if (!double.IsFinite(toleranceMeters) || toleranceMeters is < 10 or > 200)
            throw new ArgumentOutOfRangeException(parameterName, "Tolerance must be between 10 and 200 metres.");
    }

    private static void ValidateProvenance(
        SegmentSourceKind sourceKind,
        string? sourceName,
        string? sourceFormat,
        string parameterName)
    {
        if (!Enum.IsDefined(sourceKind))
            throw new ArgumentException("Choose a supported segment source.", parameterName);
        if (sourceName?.Trim().Length > 260)
            throw new ArgumentException("The segment source name cannot exceed 260 characters.", parameterName);
        if (sourceFormat?.Trim().Length > 32)
            throw new ArgumentException("The segment source format cannot exceed 32 characters.", parameterName);
    }

    private static string? TrimProvenance(string? value, int maximumLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    public static TrackPoint[] ValidatePoints(IReadOnlyList<TrackPoint> input, string parameterName)
    {
        if (input.Count > 250_000) throw new ArgumentException("A segment cannot contain more than 250,000 points.", parameterName);
        var points = input.Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (points.Length < 2) throw new ArgumentException("A segment needs at least two map points.", parameterName);
        if (points.Any(point => !double.IsFinite(point.Latitude!.Value) || point.Latitude is < -90 or > 90 ||
                                !double.IsFinite(point.Longitude!.Value) || point.Longitude is < -180 or > 180 ||
                                point.ElevationMeters.HasValue && !double.IsFinite(point.ElevationMeters.Value)))
            throw new ArgumentException("Segment points must contain finite coordinates within valid latitude and longitude ranges.", parameterName);
        return points;
    }

    private static bool HasGpsGap(IReadOnlyList<TrackPoint> points)
    {
        DateTimeOffset? prior = null;
        foreach (var point in points.Where(x => x.Timestamp.HasValue))
        {
            if (prior.HasValue && (point.Timestamp!.Value - prior.Value).TotalSeconds > 30) return true;
            prior = point.Timestamp;
        }
        return false;
    }

    private static double EstimateMovingSeconds(IReadOnlyList<TrackPoint> points, double elapsed)
    {
        if (points.Count < 2) return elapsed;
        var moving = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            if (!points[index - 1].Timestamp.HasValue || !points[index].Timestamp.HasValue) continue;
            var seconds = (points[index].Timestamp!.Value - points[index - 1].Timestamp!.Value).TotalSeconds;
            if (seconds is > 0 and <= 30 && GeometryCodec.DistanceMeters([points[index - 1], points[index]]) / seconds > 0.3) moving += seconds;
        }
        return moving > 0 ? moving : elapsed;
    }

    private static void CopyEffort(SegmentEffort source, SegmentEffort target)
    {
        target.OwnerId = source.OwnerId;
        target.SegmentId = source.SegmentId;
        target.ActivityId = source.ActivityId;
        target.StartPointIndex = source.StartPointIndex;
        target.EndPointIndex = source.EndPointIndex;
        target.StartTimeUtc = source.StartTimeUtc;
        target.ElapsedSeconds = source.ElapsedSeconds;
        target.MovingSeconds = source.MovingSeconds;
        target.AverageHeartRate = source.AverageHeartRate;
        target.AverageCadence = source.AverageCadence;
        target.AveragePowerWatts = source.AveragePowerWatts;
        target.RecordedDistanceMeters = source.RecordedDistanceMeters;
        target.ElevationGainMeters = source.ElevationGainMeters;
        target.ElevationLossMeters = source.ElevationLossMeters;
        target.AverageGradePercent = source.AverageGradePercent;
        target.AverageSpeedMetersPerSecond = source.AverageSpeedMetersPerSecond;
        target.MaxSpeedMetersPerSecond = source.MaxSpeedMetersPerSecond;
        target.MaxHeartRate = source.MaxHeartRate;
        target.MaxCadence = source.MaxCadence;
        target.MaxPowerWatts = source.MaxPowerWatts;
        target.AverageTemperatureCelsius = source.AverageTemperatureCelsius;
        target.AverageRespirationRate = source.AverageRespirationRate;
        target.CoveragePercent = source.CoveragePercent;
        target.MetricComputationVersion = source.MetricComputationVersion;
        target.Rank = source.Rank;
    }

}

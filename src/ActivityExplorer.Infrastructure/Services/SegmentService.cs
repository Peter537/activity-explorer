using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class SegmentService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    ISegmentMatcher matcher) : ISegmentService
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
                x.DistanceMeters, x.ToleranceMeters, effort?.Count ?? 0, effort?.Best, x.AverageGradePercent, x.ElevationGainMeters, x.ElevationLossMeters);
        }).ToArray();
    }

    public async Task<SegmentDetail?> GetAsync(Guid id, Guid? effortId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var segment = await db.Segments.AsNoTracking().Include(x => x.Owner).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (segment is null) return null;
        var efforts = await db.SegmentEfforts.AsNoTracking().Include(x => x.Activity)
            .Where(x => x.SegmentId == id)
            .OrderBy(x => x.ElapsedSeconds)
            .Select(x => new SegmentEffortSummary(
                x.Id, x.ActivityId, x.SegmentId, segment.Name, x.ElapsedSeconds, x.Rank, x.StartTimeUtc, x.StartPointIndex, x.EndPointIndex,
                x.MovingSeconds, x.AverageSpeedMetersPerSecond, x.MaxSpeedMetersPerSecond,
                x.AverageHeartRate, x.MaxHeartRate, x.AverageCadence, x.MaxCadence, x.AveragePowerWatts, x.MaxPowerWatts,
                x.AverageTemperatureCelsius, x.AverageRespirationRate, x.ElevationGainMeters, x.ElevationLossMeters, x.AverageGradePercent, x.CoveragePercent))
            .ToListAsync(cancellationToken);
        var selected = efforts.FirstOrDefault(x => x.Id == effortId) ?? efforts.FirstOrDefault();
        IReadOnlyList<TrackPoint> selectedPoints = [];
        if (selected is not null)
        {
            var payload = await db.ActivityStreams.AsNoTracking()
                .Where(x => x.ActivityId == selected.ActivityId).Select(x => x.CompressedPayload).SingleOrDefaultAsync(cancellationToken);
            if (payload is not null)
            {
                var all = TrackCodec.Decode(payload);
                if (selected.StartPointIndex >= 0 && selected.EndPointIndex < all.Count && selected.StartPointIndex <= selected.EndPointIndex)
                    selectedPoints = all.Skip(selected.StartPointIndex).Take(selected.EndPointIndex - selected.StartPointIndex + 1).ToArray();
            }
        }
        return new SegmentDetail(
            new SegmentSummary(segment.Id, segment.OwnerId, segment.Owner?.DisplayName ?? "Unknown profile", segment.Name,
                segment.Sport, segment.DistanceMeters, segment.ToleranceMeters, efforts.Count, efforts.Count == 0 ? null : efforts.Min(x => x.ElapsedSeconds), segment.AverageGradePercent, segment.ElevationGainMeters, segment.ElevationLossMeters),
            GeometryCodec.FromWkb(segment.GeometryWkb),
            efforts, selected?.Id, selectedPoints);
    }

    public async Task<Guid> CreateFromActivityAsync(CreateSegmentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateNameAndTolerance(request.Name, request.ToleranceMeters, nameof(request));
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var activity = await db.Activities.AsNoTracking().Include(x => x.Stream)
            .SingleOrDefaultAsync(x => x.Id == request.ActivityId && x.OwnerId == request.OwnerId, cancellationToken)
            ?? throw new InvalidOperationException("Activity was not found for this profile.");
        if (activity.Stream is null) throw new InvalidOperationException("The activity has no detailed track stream.");
        var allPoints = TrackCodec.Decode(activity.Stream.CompressedPayload);
        if (request.StartPointIndex < 0 || request.EndPointIndex >= allPoints.Count || request.EndPointIndex - request.StartPointIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "Select at least two valid track points.");
        var points = allPoints.Skip(request.StartPointIndex).Take(request.EndPointIndex - request.StartPointIndex + 1)
            .Where(x => x.Latitude.HasValue && x.Longitude.HasValue).ToArray();
        if (points.Length < 2) throw new InvalidOperationException("The selected portion has insufficient GPS data.");

        var bounds = GeometryCodec.Bounds(points);
        var distance = GeometryCodec.DistanceMeters(points);
        var elevation = Elevation(points, distance);
        var segment = new Segment
        {
            OwnerId = request.OwnerId,
            SourceActivityId = activity.Id,
            Sport = activity.Sport,
            Name = request.Name.Trim(),
            DistanceMeters = distance,
            ElevationGainMeters = elevation.Gain,
            ElevationLossMeters = elevation.Loss,
            AverageGradePercent = elevation.Grade,
            ToleranceMeters = request.ToleranceMeters,
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync(cancellationToken);
        await RecomputeAsync(segment.Id, cancellationToken);
        return segment.Id;
    }

    public async Task<Guid> CreateAsync(CreateSegmentPathRequest request, CancellationToken cancellationToken = default)
    {
        ValidateNameAndTolerance(request.Name, request.ToleranceMeters, nameof(request));
        var points = ValidatePoints(request.Points, nameof(request));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Owners.AnyAsync(x => x.Id == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The selected profile was not found.");
        if (request.SourceActivityId.HasValue && !await db.Activities.AsNoTracking()
                .AnyAsync(x => x.Id == request.SourceActivityId && x.OwnerId == request.OwnerId, cancellationToken))
            throw new InvalidOperationException("The source activity was not found for this profile.");
        var bounds = GeometryCodec.Bounds(points);
        var distance = GeometryCodec.DistanceMeters(points);
        var elevation = Elevation(points, distance);
        var segment = new Segment
        {
            OwnerId = request.OwnerId,
            SourceActivityId = request.SourceActivityId,
            Sport = request.Sport,
            Name = request.Name.Trim(),
            DistanceMeters = distance,
            ElevationGainMeters = elevation.Gain,
            ElevationLossMeters = elevation.Loss,
            AverageGradePercent = elevation.Grade,
            ToleranceMeters = request.ToleranceMeters,
            GeometryWkb = GeometryCodec.ToWkb(points)!,
            MinLatitude = bounds.MinLat!.Value,
            MinLongitude = bounds.MinLon!.Value,
            MaxLatitude = bounds.MaxLat!.Value,
            MaxLongitude = bounds.MaxLon!.Value
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync(cancellationToken);
        await RecomputeAsync(segment.Id, cancellationToken);
        return segment.Id;
    }

    public async Task RecomputeAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var segment = await db.Segments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == segmentId, cancellationToken);
        if (segment is null) return;
        var segmentPoints = GeometryCodec.FromWkb(segment.GeometryWkb);
        var latitudePadding = Math.Max(segment.ToleranceMeters, 30) / 111_000d;
        var centreLatitude = (segment.MinLatitude + segment.MaxLatitude) / 2d;
        var longitudeScale = Math.Max(Math.Abs(Math.Cos(centreLatitude * Math.PI / 180d)), 0.01d);
        var longitudePadding = Math.Min(180d, latitudePadding / longitudeScale);
        var activities = await db.Activities.AsNoTracking().Include(x => x.Stream)
            .Where(x => x.OwnerId == segment.OwnerId && x.Sport == segment.Sport && x.HasGps &&
                x.MaxLongitude >= segment.MinLongitude - longitudePadding &&
                x.MinLongitude <= segment.MaxLongitude + longitudePadding &&
                x.MaxLatitude >= segment.MinLatitude - latitudePadding &&
                x.MinLatitude <= segment.MaxLatitude + latitudePadding)
            .ToListAsync(cancellationToken);

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
                var effortDistance = GeometryCodec.DistanceMeters(slice);
                var elevation = Elevation(slice, effortDistance);
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
                    AverageHeartRate = Average(slice, x => x.HeartRate),
                    AverageCadence = Average(slice, x => x.Cadence),
                    AveragePowerWatts = Average(slice, x => x.PowerWatts),
                    ElevationGainMeters = elevation.Gain,
                    ElevationLossMeters = elevation.Loss,
                    AverageGradePercent = elevation.Grade,
                    AverageSpeedMetersPerSecond = Average(slice, x => x.SpeedMetersPerSecond),
                    MaxSpeedMetersPerSecond = Max(slice, x => x.SpeedMetersPerSecond),
                    MaxHeartRate = Max(slice, x => x.HeartRate),
                    MaxCadence = Max(slice, x => x.Cadence),
                    MaxPowerWatts = Max(slice, x => x.PowerWatts),
                    AverageTemperatureCelsius = Average(slice, x => x.TemperatureCelsius),
                    AverageRespirationRate = Average(slice, x => x.RespirationRate),
                    CoveragePercent = match.CoveragePercent
                });
            }
        }

        var existing = await db.SegmentEfforts.Where(x => x.SegmentId == segmentId).ToListAsync(cancellationToken);
        db.SegmentEfforts.RemoveRange(existing);
        var ranked = generated.OrderBy(x => x.ElapsedSeconds).ToArray();
        for (var index = 0; index < ranked.Length; index++) ranked[index].Rank = index + 1;
        db.SegmentEfforts.AddRange(ranked);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateNameAndTolerance(string name, double toleranceMeters, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", parameterName);
        if (name.Trim().Length > 240) throw new ArgumentException("Name cannot exceed 240 characters.", parameterName);
        if (!double.IsFinite(toleranceMeters) || toleranceMeters is < 10 or > 200)
            throw new ArgumentOutOfRangeException(parameterName, "Tolerance must be between 10 and 200 metres.");
    }

    private static TrackPoint[] ValidatePoints(IReadOnlyList<TrackPoint> input, string parameterName)
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

    private static double? Average(IEnumerable<TrackPoint> points, Func<TrackPoint, double?> selector)
    {
        var values = points.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? Max(IEnumerable<TrackPoint> points, Func<TrackPoint, double?> selector)
    {
        var values = points.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    private static (double? Gain, double? Loss, double? Grade) Elevation(IReadOnlyList<TrackPoint> points, double distance)
    {
        var values = points.Where(x => x.ElevationMeters.HasValue).Select(x => x.ElevationMeters!.Value).ToArray();
        if (values.Length < 2) return (null, null, null);
        var gain = 0d;
        var loss = 0d;
        for (var index = 1; index < values.Length; index++)
        {
            var change = values[index] - values[index - 1];
            if (change > 0) gain += change;
            else loss -= change;
        }
        double? grade = distance > 0 ? (values[^1] - values[0]) / distance * 100d : null;
        return (gain, loss, grade);
    }


}

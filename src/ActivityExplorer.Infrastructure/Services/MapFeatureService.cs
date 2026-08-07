using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class MapFeatureService(IDbContextFactory<ExplorerDbContext> contextFactory) : IMapFeatureService
{
    public async Task<MapFeatureCollection> GetActivitiesAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = db.Activities.AsNoTracking().Include(x => x.Owner).Where(x => x.HasGps && x.SimplifiedGeometryWkb != null);
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        if (query.Sport.HasValue) source = source.Where(x => x.Sport == query.Sport);
        source = ApplyDates(source, query);
        source = ApplyBounds(source, query);
        var rows = await source.OrderByDescending(x => x.StartTimeUtc).Take(2000).ToListAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(
            x.SimplifiedGeometryWkb!,
            new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["kind"] = "activity",
                ["title"] = x.Title,
                ["sport"] = x.Sport.ToString(),
                ["owner"] = x.Owner?.DisplayName,
                ["date"] = x.StartTimeUtc,
                ["distanceMeters"] = x.DistanceMeters
            })));
    }

    public async Task<MapFeatureCollection> GetRoutesAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = db.Routes.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        if (query.Sport.HasValue) source = source.Where(x => x.Sport == query.Sport);
        source = ApplyBounds(source, query);
        var rows = await source.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(1000).ToListAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(x.GeometryWkb, new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["kind"] = "route",
            ["title"] = x.Name,
            ["sport"] = x.Sport.ToString(),
            ["owner"] = x.Owner?.DisplayName,
            ["distanceMeters"] = x.DistanceMeters
        })));
    }

    public async Task<MapFeatureCollection> GetSegmentsAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = db.Segments.AsNoTracking().Include(x => x.Owner).AsQueryable();
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        if (query.Sport.HasValue) source = source.Where(x => x.Sport == query.Sport);
        source = ApplyBounds(source, query);
        var rows = await source.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(1000).ToListAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(x.GeometryWkb, new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["kind"] = "segment",
            ["title"] = x.Name,
            ["sport"] = x.Sport.ToString(),
            ["owner"] = x.Owner?.DisplayName,
            ["distanceMeters"] = x.DistanceMeters
        })));
    }

    private static IQueryable<Activity> ApplyDates(IQueryable<Activity> source, MapQuery query)
    {
        if (query.From.HasValue)
        {
            var from = new DateTimeOffset(query.From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            source = source.Where(x => x.StartTimeUtc >= from);
        }
        if (query.To.HasValue)
        {
            var to = new DateTimeOffset(query.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            source = source.Where(x => x.StartTimeUtc < to);
        }
        return source;
    }

    private static IQueryable<Activity> ApplyBounds(IQueryable<Activity> source, MapQuery query)
    {
        if (!HasBounds(query)) return source;
        source = source.Where(x => x.MaxLatitude >= query.South && x.MinLatitude <= query.North);
        return query.West <= query.East
            ? source.Where(x =>
                x.MinLongitude <= x.MaxLongitude && x.MaxLongitude >= query.West && x.MinLongitude <= query.East ||
                x.MinLongitude > x.MaxLongitude && (x.MinLongitude <= query.East || x.MaxLongitude >= query.West))
            : source.Where(x => x.MinLongitude > x.MaxLongitude ||
                x.MaxLongitude >= query.West || x.MinLongitude <= query.East);
    }

    private static IQueryable<Route> ApplyBounds(IQueryable<Route> source, MapQuery query)
    {
        if (!HasBounds(query)) return source;
        source = source.Where(x => x.MaxLatitude >= query.South && x.MinLatitude <= query.North);
        return query.West <= query.East
            ? source.Where(x =>
                x.MinLongitude <= x.MaxLongitude && x.MaxLongitude >= query.West && x.MinLongitude <= query.East ||
                x.MinLongitude > x.MaxLongitude && (x.MinLongitude <= query.East || x.MaxLongitude >= query.West))
            : source.Where(x => x.MinLongitude > x.MaxLongitude ||
                x.MaxLongitude >= query.West || x.MinLongitude <= query.East);
    }

    private static IQueryable<Segment> ApplyBounds(IQueryable<Segment> source, MapQuery query)
    {
        if (!HasBounds(query)) return source;
        source = source.Where(x => x.MaxLatitude >= query.South && x.MinLatitude <= query.North);
        return query.West <= query.East
            ? source.Where(x =>
                x.MinLongitude <= x.MaxLongitude && x.MaxLongitude >= query.West && x.MinLongitude <= query.East ||
                x.MinLongitude > x.MaxLongitude && (x.MinLongitude <= query.East || x.MaxLongitude >= query.West))
            : source.Where(x => x.MinLongitude > x.MaxLongitude ||
                x.MaxLongitude >= query.West || x.MinLongitude <= query.East);
    }

    private static bool HasBounds(MapQuery query) =>
        query.West.HasValue && query.South.HasValue && query.East.HasValue && query.North.HasValue;

    private static void Validate(MapQuery query)
    {
        var values = new[] { query.West, query.South, query.East, query.North };
        var count = values.Count(value => value.HasValue);
        if (count is not 0 and not 4) throw new ArgumentException("Map bounds must include west, south, east, and north.", nameof(query));
        if (values.Any(value => value.HasValue && !double.IsFinite(value.Value)))
            throw new ArgumentException("Map bounds must be finite numbers.", nameof(query));
        if (HasBounds(query) && (query.West is < -180 or > 180 || query.East is < -180 or > 180 ||
                                 query.South is < -90 or > 90 || query.North is < -90 or > 90 || query.South > query.North))
            throw new ArgumentException("Map bounds contain an invalid latitude or longitude range.", nameof(query));
        if (query.Zoom is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(query), "Map zoom must be between 0 and 24.");
        if (query.From.HasValue && query.To.HasValue && query.From > query.To)
            throw new ArgumentException("The map start date cannot be after the end date.", nameof(query));
    }

    private static MapFeature Feature(byte[] wkb, IReadOnlyDictionary<string, object?> properties)
    {
        var coordinates = GeometryCodec.FromWkb(wkb)
            .Where(x => x.Longitude.HasValue && x.Latitude.HasValue)
            .Select(x => new[] { x.Longitude!.Value, x.Latitude!.Value })
            .ToArray();
        return new MapFeature("Feature", new MapGeometry("LineString", coordinates), properties);
    }

    private static MapFeatureCollection Collection(IEnumerable<MapFeature> features) =>
        new("FeatureCollection", features.Where(x => x.Geometry.Coordinates.Count > 1).ToArray());
}

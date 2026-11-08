using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class MapFeatureService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider? timeProvider = null) : IMapFeatureService
{
    private const int ActivityLimit = 2000;
    private const int DefinitionLimit = 1000;
    // A WKB line needs a 9-byte header and at least two 16-byte XY coordinates.
    private const int MinimumLineGeometryBytes = 41;
    public async Task<MapFeatureCollection> GetActivitiesAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        var defaultMap = query.Criteria is null && !query.Sport.HasValue && !query.From.HasValue && !query.To.HasValue &&
            !query.Period.HasValue && !query.West.HasValue;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var mapping = defaultMap ? await SqliteReadMapping.BeginAsync(connection, cancellationToken) : null;
        await using var transaction = await ReadSnapshot.BeginOnOpenConnectionAsync(db, cancellationToken);
        var criteria = await ActivityCriteriaQuery.ResolveAsync(db, query.OwnerId,
            query.Criteria ?? new(Sport: query.Sport), cancellationToken);
        var periods = await ReportingDateQuery.ResolveAsync(db, query.OwnerId,
            ReportingDateQuery.Selection(query.Period, query.From, query.To),
            query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken);
        // The partial index avoids reading every geometry for the default unbounded map.
        // Older read-only databases may not have been upgraded, so retain the ordinary query there.
        var indexed = defaultMap && await db.Database.SqlQueryRaw<long>("""
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'index' AND name = 'IX_Activities_MapCandidates') AS "Value"
            """).SingleAsync(cancellationToken) != 0;
        var activities = indexed
            ? db.Activities.FromSqlRaw("""
                SELECT * FROM "Activities" INDEXED BY "IX_Activities_MapCandidates"
                WHERE "HasGps" AND "SimplifiedGeometryWkb" IS NOT NULL AND length("SimplifiedGeometryWkb") >= 41
                """)
            : db.Activities;
        var source = activities.AsNoTracking().Where(x => x.HasGps &&
            x.SimplifiedGeometryWkb != null && x.SimplifiedGeometryWkb.Length >= MinimumLineGeometryBytes);
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        source = ActivityCriteriaQuery.Apply(db, source, criteria);
        source = ReportingDateQuery.Apply(source, periods);
        source = ApplyBounds(source, query);
        var matching = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(x => x.StartTimeUtc).ThenBy(x => x.Id).Take(ActivityLimit)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Sport,
                OwnerName = x.Owner == null ? null : x.Owner.DisplayName,
                x.StartTimeUtc,
                x.DistanceMeters,
                x.SimplifiedGeometryWkb
            }).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(
            x.SimplifiedGeometryWkb!,
            new Dictionary<string, object?>
            {
                ["id"] = x.Id,
                ["kind"] = "activity",
                ["title"] = x.Title,
                ["sport"] = x.Sport.ToString(),
                ["owner"] = x.OwnerName,
                ["date"] = x.StartTimeUtc,
                ["distanceMeters"] = x.DistanceMeters
            })), matching, ActivityLimit);
    }

    public async Task<MapFeatureCollection> GetRoutesAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var source = db.Routes.AsNoTracking().Where(x => x.GeometryWkb.Length >= MinimumLineGeometryBytes);
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        if (query.Sport.HasValue) source = source.Where(x => x.Sport == query.Sport);
        source = ApplyBounds(source, query);
        var matching = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(DefinitionLimit).Select(x => new
            {
                x.Id,
                x.Name,
                x.Sport,
                OwnerName = x.Owner == null ? null : x.Owner.DisplayName,
                x.DistanceMeters,
                x.GeometryWkb
            })
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(x.GeometryWkb, new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["kind"] = "route",
            ["title"] = x.Name,
            ["sport"] = x.Sport.ToString(),
            ["owner"] = x.OwnerName,
            ["distanceMeters"] = x.DistanceMeters
        })), matching, DefinitionLimit);
    }

    public async Task<MapFeatureCollection> GetSegmentsAsync(MapQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var source = db.Segments.AsNoTracking().Where(x => x.GeometryWkb.Length >= MinimumLineGeometryBytes);
        if (query.OwnerId.HasValue) source = source.Where(x => x.OwnerId == query.OwnerId);
        if (query.Sport.HasValue) source = source.Where(x => x.Sport == query.Sport);
        source = ApplyBounds(source, query);
        var matching = await source.CountAsync(cancellationToken);
        var rows = await source.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Take(DefinitionLimit).Select(x => new
            {
                x.Id,
                x.Name,
                x.Sport,
                OwnerName = x.Owner == null ? null : x.Owner.DisplayName,
                x.DistanceMeters,
                x.GeometryWkb
            })
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Collection(rows.Select(x => Feature(x.GeometryWkb, new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["kind"] = "segment",
            ["title"] = x.Name,
            ["sport"] = x.Sport.ToString(),
            ["owner"] = x.OwnerName,
            ["distanceMeters"] = x.DistanceMeters
        })), matching, DefinitionLimit);
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
        ReportingDates.Validate(ReportingDateQuery.Selection(query.Period, query.From, query.To));
    }

    private static MapFeature Feature(byte[] wkb, IReadOnlyDictionary<string, object?> properties) =>
        new("Feature", new MapGeometry("LineString", GeometryCodec.MapCoordinates(wkb)), properties);

    private static MapFeatureCollection Collection(IEnumerable<MapFeature> features, int matching, int limit)
    {
        var lines = features.Where(x => x.Geometry.Coordinates.Count > 1).ToArray();
        return new("FeatureCollection", lines, new(lines.Length, matching, limit));
    }
}

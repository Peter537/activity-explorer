using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class MapFeatureService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider? timeProvider = null) : IMapFeatureService
{
    private const int ActivityLimit = 2000;
    private const int DefinitionLimit = 1000;
    // A WKB line needs a 9-byte header and at least two 16-byte XY coordinates.
    private const int MinimumLineGeometryBytes = 41;

    private enum Layer { Activity, Route, Segment }
    private sealed record Line(Guid Id, string Title, SportKind Sport, string? Owner, double DistanceMeters, DateTimeOffset? Date);

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
            .Select(x => new Line(x.Id, x.Title.Substring(0, 240), x.Sport,
                x.Owner == null ? null : x.Owner.DisplayName.Substring(0, 120), x.DistanceMeters, x.StartTimeUtc))
            .ToArrayAsync(cancellationToken);
        var result = await ReadLinesAsync(db, rows, Layer.Activity, query.Zoom, matching, ActivityLimit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
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
            .Take(DefinitionLimit).Select(x => new Line(x.Id, x.Name.Substring(0, 240), x.Sport,
                x.Owner == null ? null : x.Owner.DisplayName.Substring(0, 120), x.DistanceMeters, null))
            .ToArrayAsync(cancellationToken);
        var result = await ReadLinesAsync(db, rows, Layer.Route, query.Zoom, matching, DefinitionLimit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
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
            .Take(DefinitionLimit).Select(x => new Line(x.Id, x.Name.Substring(0, 240), x.Sport,
                x.Owner == null ? null : x.Owner.DisplayName.Substring(0, 120), x.DistanceMeters, null))
            .ToArrayAsync(cancellationToken);
        var result = await ReadLinesAsync(db, rows, Layer.Segment, query.Zoom, matching, DefinitionLimit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
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

    private static async Task<MapFeatureCollection> ReadLinesAsync(ExplorerDbContext db, Line[] rows, Layer layer,
        int zoom, int matching, int limit, CancellationToken cancellationToken)
    {
        if (rows.Length == 0) return new("FeatureCollection", [], new(0, matching, limit));
        var (table, column, kind, sql) = layer switch
        {
            Layer.Activity => ("Activities", "SimplifiedGeometryWkb", "activity", """
                SELECT rowid, "Id", length("SimplifiedGeometryWkb") FROM "Activities"
                WHERE "Id" IN (SELECT value FROM json_each($ids))
                """),
            Layer.Route => ("Routes", "GeometryWkb", "route", """
                SELECT rowid, "Id", length("GeometryWkb") FROM "Routes"
                WHERE "Id" IN (SELECT value FROM json_each($ids))
                """),
            _ => ("Segments", "GeometryWkb", "segment", """
                SELECT rowid, "Id", length("GeometryWkb") FROM "Segments"
                WHERE "Id" IN (SELECT value FROM json_each($ids))
                """)
        };
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(rows.Select(row => row.Id.ToString("D").ToUpperInvariant())));
        var blobs = new List<(Guid Id, long RowId, int Size)>(rows.Length);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                blobs.Add((reader.GetGuid(1), reader.GetInt64(0), reader.GetInt32(2)));
        }

        // Short lines release their unused allowance before sharing the remainder among dense lines.
        var remaining = VertexBudget(layer, zoom);
        var remainingRows = blobs.Count;
        var perLine = zoom switch { < 6 => 96, < 12 => 256, _ => 512 };
        var coordinates = new Dictionary<Guid, IReadOnlyList<double[]>>(blobs.Count);
        foreach (var blob in blobs.OrderBy(x => x.Size))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new SqliteBlob(connection, table, column, blob.RowId, readOnly: true);
            var points = MapLineWkb.Read(stream, Math.Min(perLine, remaining / remainingRows), cancellationToken);
            remainingRows--;
            if (points.Length < 2) continue;
            remaining -= points.Length;
            coordinates.Add(blob.Id, points);
        }

        var lines = new List<MapFeature>(coordinates.Count);
        foreach (var row in rows)
        {
            if (!coordinates.TryGetValue(row.Id, out var points)) continue;
            var properties = new Dictionary<string, object?>
            {
                ["id"] = row.Id,
                ["kind"] = kind,
                ["title"] = row.Title,
                ["sport"] = row.Sport.ToString(),
                ["owner"] = row.Owner,
                ["distanceMeters"] = double.IsFinite(row.DistanceMeters) ? row.DistanceMeters : null
            };
            if (row.Date.HasValue) properties["date"] = row.Date.Value;
            lines.Add(new("Feature", new("LineString", points), properties));
        }
        return new("FeatureCollection", lines, new(lines.Count, matching, limit));
    }

    private static int VertexBudget(Layer layer, int zoom) => (layer, zoom) switch
    {
        (Layer.Activity, < 6) => 16_384,
        (Layer.Activity, < 12) => 24_576,
        (Layer.Activity, _) => 32_768,
        (_, < 6) => 8_192,
        (_, < 12) => 12_288,
        _ => 16_384
    };
}

using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Map_dense_geometry_obeys_zoom_local_and_layer_budgets_and_preserves_full_wrapped_routes()
    {
        const int vertices = 8192;
        const int routeCount = 112;
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Wrapped map geometry");
        var isolatedOwner = await setup.SeedOwnerAsync("Single dense route");
        var points = Enumerable.Range(0, vertices).Select(index =>
        {
            var fraction = (double)index / (vertices - 1);
            var longitude = 179.25 + fraction * 2;
            var latitude = 50 + Math.Sin(fraction * 4 * Math.PI) / 3;
            return new TrackPoint(null, latitude, longitude > 180 ? longitude - 360 : longitude, null, null, null, null, null, null, null);
        }).ToArray();
        var fullGeometry = Assert.IsType<byte[]>(GeometryCodec.ToWkb(points));
        var capturedIds = new List<Guid>(routeCount);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 0; index < routeCount; index++)
            {
                var row = Route("Dense route", fullGeometry, index);
                capturedIds.Add(row.Id);
                db.Routes.Add(row);
            }
            db.Routes.Add(Route("Invalid geometry", new byte[64], routeCount));
            var isolated = Route("Single dense route", fullGeometry, 0);
            isolated.OwnerId = isolatedOwner;
            db.Routes.Add(isolated);
            await db.SaveChangesAsync();
        }

        var service = new MapFeatureService(setup.Factory);
        var view = new MapQuery(OwnerId: owner, West: 179, South: 49, East: -178, North: 51, Zoom: 4);
        var start = Stopwatch.GetTimestamp();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var wide = await service.GetRoutesAsync(view);
        var wideBytes = JsonSerializer.SerializeToUtf8Bytes(wide, JsonSerializerOptions.Web).Length;
        TestContext.Current.TestOutputHelper!.WriteLine($"Dense map routes ({routeCount} × {vertices} vertices). WKB {routeCount * (long)fullGeometry.Length:N0} bytes; display {wideBytes:N0} bytes; elapsed {Stopwatch.GetElapsedTime(start).TotalMilliseconds:N1} ms; process allocation delta {GC.GetTotalAllocatedBytes(precise: true) - allocated:N0} bytes.");
        var close = await service.GetRoutesAsync(view with { Zoom = 14 });
        foreach (var (zoom, limit) in new[] { (4, 96), (8, 256), (14, 512) })
        {
            // With only one eligible route, the layer budget cannot mask a missing per-route cap.
            var single = await service.GetRoutesAsync(view with { OwnerId = isolatedOwner, Zoom = zoom });
            var coordinates = Assert.Single(single.Features).Geometry.Coordinates;
            Assert.InRange(coordinates.Count, 2, limit);
            Assert.Equal([points[0].Longitude!.Value, points[0].Latitude!.Value], coordinates[0]);
            Assert.Equal([points[^1].Longitude!.Value, points[^1].Latitude!.Value], coordinates[^1]);
        }
        Assert.Equal(new MapLayerCoverage(routeCount, routeCount + 1, 1000), wide.Coverage);
        Assert.True(wide.Coverage!.Truncated);
        var wideVertices = wide.Features.Sum(feature => feature.Geometry.Coordinates.Count);
        var closeVertices = close.Features.Sum(feature => feature.Geometry.Coordinates.Count);
        Assert.InRange(wideVertices, routeCount * 2, 8192);
        Assert.InRange(closeVertices, wideVertices + 1, 16384);
        Assert.InRange(wideBytes, 1, 450_000);
        Assert.Equal(capturedIds.AsEnumerable().Reverse(), wide.Features.Select(feature => Assert.IsType<Guid>(feature.Properties["id"])));
        foreach (var feature in close.Features)
        {
            var coordinates = feature.Geometry.Coordinates;
            Assert.Equal([points[0].Longitude!.Value, points[0].Latitude!.Value], coordinates[0]);
            Assert.Equal([points[^1].Longitude!.Value, points[^1].Latitude!.Value], coordinates[^1]);
            var unwrapped = coordinates.Select(point => point[0] < 0 ? point[0] + 360 : point[0]).ToArray();
            Assert.Equal(unwrapped.Order(), unwrapped);
            Assert.Contains(coordinates, point => point[1] > 50.32);
            Assert.Contains(coordinates, point => point[1] < 49.68);
        }

        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(fullGeometry, await verification.Routes.Where(x => x.Id == capturedIds[0]).Select(x => x.GeometryWkb).SingleAsync());

        Route Route(string name, byte[] geometry, int index) => new()
        {
            OwnerId = owner,
            Name = name,
            Sport = SportKind.Cycling,
            GeometryWkb = geometry,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero).AddSeconds(index),
            MinLongitude = 179.25,
            MaxLongitude = -178.75,
            MinLatitude = 49.6,
            MaxLatitude = 50.4
        };
    }

    [Theory]
    [InlineData("activities", 2000)]
    [InlineData("routes", 1000)]
    [InlineData("segments", 1000)]
    public async Task Map_coverage_counts_the_same_eligible_scope_at_zero_exact_limit_and_over_limit(string kind, int limit)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Map coverage");
        var other = await setup.SeedOwnerAsync("Other map coverage");
        var map = new MapFeatureService(setup.Factory);
        var query = new MapQuery(owner, SportKind.Cycling, new(2026, 1, 2), new(2026, 1, 2),
            West: -1, South: -1, East: 2, North: 2);
        Task<MapFeatureCollection> Read() => kind switch
        {
            "activities" => map.GetActivitiesAsync(query),
            "routes" => map.GetRoutesAsync(query),
            _ => map.GetSegmentsAsync(query)
        };
        var empty = await Read();
        Assert.Empty(empty.Features);
        Assert.Equal(new MapLayerCoverage(0, 0, limit), empty.Coverage);
        var geometry = Assert.IsType<byte[]>(GeometryCodec.ToWkb([
            new TrackPoint(null, 0, 0, null, null, null, null, null, null, null),
            new TrackPoint(null, 1, 1, null, null, null, null, null, null, null)]));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 1; index <= limit; index++) db.Add(Row(index, owner, SportKind.Cycling, geometry));
            db.Add(Row(limit + 10, other, SportKind.Cycling, geometry));
            db.Add(Row(limit + 11, owner, SportKind.Running, geometry));
            db.Add(Row(limit + 12, owner, SportKind.Cycling, geometry, 20));
            db.Add(Row(limit + 13, owner, SportKind.Cycling, []));
            if (kind == "activities")
            {
                var noGps = (Activity)Row(limit + 14, owner, SportKind.Cycling, geometry);
                noGps.HasGps = false;
                db.Add(noGps);
                var missingGeometry = (Activity)Row(limit + 15, owner, SportKind.Cycling, geometry);
                missingGeometry.SimplifiedGeometryWkb = null;
                db.Add(missingGeometry);
                var wrongDate = (Activity)Row(limit + 16, owner, SportKind.Cycling, geometry);
                wrongDate.StartTimeUtc = new(2026, 1, 3, 12, 0, 0, TimeSpan.Zero);
                db.Add(wrongDate);
            }
            await db.SaveChangesAsync();
        }
        var exact = await Read();
        Assert.Equal(limit, exact.Features.Count);
        Assert.Equal(new MapLayerCoverage(limit, limit, limit), exact.Coverage);
        Assert.False(exact.Coverage!.Truncated);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Add(Row(limit + 1, owner, SportKind.Cycling, geometry));
            await db.SaveChangesAsync();
        }
        var capped = await Read();
        Assert.Equal(new MapLayerCoverage(limit, limit + 1, limit), capped.Coverage);
        Assert.True(capped.Coverage!.Truncated);
        Assert.Equal(exact.Features.Select(feature => feature.Properties["id"]), capped.Features.Select(feature => feature.Properties["id"]));
        Assert.Equal(Id(1), capped.Features[0].Properties["id"]);
        Assert.Equal(Id(limit), capped.Features[^1].Properties["id"]);

        object Row(int index, Guid rowOwner, SportKind sport, byte[] wkb, double longitude = 0)
        {
            var instant = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
            return kind switch
            {
                "activities" => new Activity
                {
                    Id = Id(index),
                    OwnerId = rowOwner,
                    Sport = sport,
                    Title = $"Path {index}",
                    NaturalFingerprint = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StartTimeUtc = instant,
                    HasGps = true,
                    SimplifiedGeometryWkb = wkb,
                    MinLatitude = 0,
                    MaxLatitude = 1,
                    MinLongitude = longitude,
                    MaxLongitude = longitude + 1
                },
                "routes" => new Route
                {
                    Id = Id(index),
                    OwnerId = rowOwner,
                    Sport = sport,
                    Name = $"Path {index}",
                    GeometryWkb = wkb,
                    CreatedAtUtc = instant,
                    MinLatitude = 0,
                    MaxLatitude = 1,
                    MinLongitude = longitude,
                    MaxLongitude = longitude + 1
                },
                _ => new Segment
                {
                    Id = Id(index),
                    OwnerId = rowOwner,
                    Sport = sport,
                    Name = $"Path {index}",
                    GeometryWkb = wkb,
                    CreatedAtUtc = instant,
                    MinLatitude = 0,
                    MaxLatitude = 1,
                    MinLongitude = longitude,
                    MaxLongitude = longitude + 1
                }
            };
        }
        static Guid Id(int index) => Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}");
    }
}

using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
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

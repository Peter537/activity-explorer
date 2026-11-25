using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData("Both wrap", true, true)]
    [InlineData("Only the activity wraps", false, true)]
    [InlineData("Padding wraps", false, false)]
    public async Task Segment_matching_reaches_antimeridian_candidates_in_sqlite(
        string geography, bool segmentWraps, bool activityWraps)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Boundary athlete");
        var start = new DateTimeOffset(2026, 6, 4, 8, 0, 0, TimeSpan.Zero);
        var activity = geography == "Padding wraps"
            ? Enumerable.Range(0, 8).Select(index => Point(index, 70 + index * 0.0003, -179.9998)).ToArray()
            : new[] { 179.99, 179.994, 179.997, 179.999, -179.999, -179.995 }
                .Select((longitude, index) => Point(index, 1, longitude)).ToArray();
        var segment = geography switch
        {
            "Both wrap" => activity,
            "Only the activity wraps" => activity[..3],
            _ => activity.Select(point => point with { Longitude = 179.9998 }).ToArray()
        };

        var activityBounds = GeometryCodec.Bounds(activity);
        var segmentBounds = GeometryCodec.Bounds(segment);
        Assert.Equal(activityWraps, activityBounds.MinLon > activityBounds.MaxLon);
        Assert.Equal(segmentWraps, segmentBounds.MinLon > segmentBounds.MaxLon);
        var activityId = await setup.SeedActivityAsync(owner, geography, SportKind.Running, activity);
        await setup.SeedActivityAsync(owner, "Distant track", SportKind.Running,
            activity.Select(point => point with { Longitude = 0 }).ToArray());

        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var segmentId = await service.CreateAsync(new CreateSegmentPathRequest(owner, geography, SportKind.Running, segment));

        await using var db = await setup.Factory.CreateDbContextAsync();
        var effort = Assert.Single(await db.SegmentEfforts.Where(x => x.SegmentId == segmentId).ToListAsync());
        Assert.Equal(activityId, effort.ActivityId);
        Assert.Equal(0, effort.StartPointIndex);
        Assert.Equal(segment.Length - 1, effort.EndPointIndex);

        TrackPoint Point(int index, double latitude, double longitude) =>
            new(start.AddSeconds(index * 5), latitude, longitude, null, null, null, null, null, null, null);
    }
}

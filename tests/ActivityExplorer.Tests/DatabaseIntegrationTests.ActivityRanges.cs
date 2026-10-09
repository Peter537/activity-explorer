using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Exact_attempt_links_match_detail_stream_identity_through_transfer_replacement_and_deletion()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = await setup.SeedOwnerAsync("Range athlete");
        var second = await setup.SeedOwnerAsync("Transferred range athlete");
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 21).Select(index => new TrackPoint(
            start.AddSeconds(index), 55, 12 + index * 0.0001, index * 10, null, null, null, null, 200, null)).ToArray();
        var id = await setup.SeedActivityAsync(first, "Exact range source", SportKind.Cycling, points);
        await using (var db = setup.Factory.CreateDbContext())
        {
            (await db.Activities.SingleAsync(activity => activity.Id == id)).StartTimeUtc = start.AddSeconds(-30);
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(setup.Factory);
        var storage = CreateStorageServices(setup);
        var activities = new ActivityQueryService(setup.Factory, statistics,
            new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock()),
            storage.Originals, storage.FileOperations, storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        var query = new RecordAttemptQuery(SportKind.Cycling, RecordKind.PowerCurve, "5 s", first);
        var attempt = Assert.Single((await statistics.GetAttemptsAsync(query))!.Attempts.Items);
        var detail = (await activities.GetAsync(id))!;
        Assert.NotNull(attempt.Range);
        Assert.NotNull(attempt.StreamFingerprint);
        Assert.Equal(detail.StreamFingerprint, attempt.StreamFingerprint);
        Assert.Equal(30, attempt.StartSeconds);
        Assert.Equal(start, attempt.Range.Start.Timestamp);
        Assert.Equal(0, attempt.Range.Start.Position);
        Assert.Null(ActivityRangeBoundary.Validate(detail.Points, attempt.Range));

        await activities.UpdateAsync(id, new("Transferred source", null, null, second));
        detail = (await activities.GetAsync(id))!;
        Assert.Equal(second, detail.Summary.OwnerId);
        Assert.Equal(attempt.StreamFingerprint, detail.StreamFingerprint);
        Assert.Empty((await statistics.GetAttemptsAsync(query))!.Attempts.Items);
        Assert.Equal(attempt.StreamFingerprint,
            Assert.Single((await statistics.GetAttemptsAsync(query with { OwnerId = second }))!.Attempts.Items).StreamFingerprint);

        await using (var db = setup.Factory.CreateDbContext())
        {
            var activity = await db.Activities.Include(item => item.Stream).Include(item => item.Laps).Include(item => item.Metrics)
                .AsSplitQuery().SingleAsync(item => item.Id == id);
            ImportProcessor.ReplaceTechnicalData(activity, new ParsedActivity
            {
                Title = "Replacement technical source",
                Sport = SportKind.Cycling,
                StartTimeUtc = start,
                Points = points.Select(point => point with { PowerWatts = 250 }).ToArray()
            });
            await db.SaveChangesAsync();
        }
        detail = (await activities.GetAsync(id))!;
        Assert.NotEqual(attempt.StreamFingerprint, detail.StreamFingerprint);
        // Equal sample count and unchanged timestamps alone cannot identify replacement content.
        Assert.Null(ActivityRangeBoundary.Validate(detail.Points, attempt.Range));
        var replacement = Assert.Single((await statistics.GetAttemptsAsync(query with { OwnerId = second }))!.Attempts.Items);
        Assert.Equal(detail.StreamFingerprint, replacement.StreamFingerprint);
        Assert.Equal(250, replacement.Value);
        await activities.DeleteAsync([id]);
        Assert.Null(await activities.GetAsync(id));
        Assert.Empty((await statistics.GetAttemptsAsync(query with { OwnerId = second }))!.Attempts.Items);
    }
}

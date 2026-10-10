using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    private static readonly int[] ThreeChildPassStarts = [20, 100, 180];
    [Fact]
    public async Task Segment_comparison_reads_only_requested_streams_in_one_snapshot_and_reverses_delta()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Comparison athlete");
        var points = TestSupport.Track(101);
        var fastest = await setup.SeedActivityAsync(owner, "Fastest unrelated stream", SportKind.Cycling, points);
        var first = await setup.SeedActivityAsync(owner, "Baseline", SportKind.Cycling, ComparisonTiming(points, 2, 1));
        var second = await setup.SeedActivityAsync(owner, "Comparison", SportKind.Cycling, ComparisonTiming(points, 3, 2));
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var segment = await service.CreateFromActivityAsync(new(owner, fastest, "Synthetic climb", 0, 100));
        var detail = (await service.GetAsync(segment))!;
        var baseline = detail.Efforts.Single(effort => effort.ActivityId == first);
        var comparison = detail.Efforts.Single(effort => effort.ActivityId == second);
        await using (var db = setup.Factory.CreateDbContext())
            await db.ActivityStreams.Where(stream => stream.ActivityId == fastest)
                .ExecuteUpdateAsync(update => update.SetProperty(stream => stream.CompressedPayload, new byte[] { 1, 2, 3 }));
        var capture = new ReportingCommandCapture();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}")
            .AddInterceptors(capture).Options;
        service = new SegmentService(new TestDbFactory(options), new SegmentMatcher(), new OwnerMutationLock());

        var result = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;

        Assert.Equal(SegmentComparisonUnavailableReason.None, result.UnavailableReason);
        Assert.Equal(3, result.Detail.Efforts.Count);
        Assert.Empty(result.Detail.SelectedEffortPoints);
        Assert.Equal(100, result.Samples[^1].DeltaSeconds, 6);
        Assert.Single(capture.Commands, command => command.Sql.Contains("CompressedPayload", StringComparison.Ordinal));
        var transaction = capture.Commands[0].Transaction;
        Assert.NotNull(transaction);
        Assert.All(capture.Commands, command => Assert.Same(transaction, command.Transaction));
        var swapped = (await service.GetComparisonAsync(segment, comparison.Id, baseline.Id))!;
        Assert.Equal(result.Samples.Select(sample => sample.DistanceMeters), swapped.Samples.Select(sample => sample.DistanceMeters));
        Assert.Equal(result.Samples.Select(sample => sample.DeltaSeconds), swapped.Samples.Select(sample => -sample.DeltaSeconds));
        await using (var db = setup.Factory.CreateDbContext())
        {
            await db.SegmentEfforts.Where(effort => effort.Id == baseline.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(effort => effort.ElapsedSeconds, baseline.ElapsedSeconds + 0.00075));
            await db.SegmentEfforts.Where(effort => effort.Id == comparison.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(effort => effort.ElapsedSeconds, comparison.ElapsedSeconds - 0.00075));
        }
        var mismatch = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;
        Assert.Equal(SegmentComparisonUnavailableReason.DurationMismatch, mismatch.UnavailableReason);
        Assert.Empty(mismatch.Samples);
    }

    [Fact]
    public async Task Segment_comparison_keeps_exact_ids_and_rejects_scope_changes_missing_streams_and_deleted_efforts()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Original comparison athlete");
        var other = await setup.SeedOwnerAsync("Other comparison athlete");
        var points = TestSupport.Track(81);
        var first = await setup.SeedActivityAsync(owner, "First pass", SportKind.Running, points);
        var second = await setup.SeedActivityAsync(owner, "Second pass", SportKind.Running, ComparisonTiming(points, 2, 1));
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var segment = await service.CreateFromActivityAsync(new(owner, first, "Straight path", 0, 80));
        var detail = (await service.GetAsync(segment))!;
        var baseline = detail.Efforts.Single(effort => effort.ActivityId == first);
        var comparison = detail.Efforts.Single(effort => effort.ActivityId == second);
        foreach (var invalid in new[] { Guid.Empty, Guid.NewGuid(), baseline.Id })
        {
            var result = (await service.GetComparisonAsync(segment, baseline.Id, invalid))!;
            Assert.Equal(SegmentComparisonUnavailableReason.InvalidEfforts, result.UnavailableReason);
            Assert.Equal(segment, result.Detail.Summary.Id);
            Assert.Empty(result.Samples);
        }
        Assert.Null(await service.GetComparisonAsync(Guid.NewGuid(), baseline.Id, comparison.Id));
        await service.RecomputeAsync(segment);
        Assert.Equal(SegmentComparisonUnavailableReason.None, (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!.UnavailableReason);
        await using (var db = setup.Factory.CreateDbContext())
            await db.Activities.Where(activity => activity.Id == second).ExecuteUpdateAsync(update => update.SetProperty(activity => activity.Sport, SportKind.Cycling));
        Assert.Equal(SegmentComparisonUnavailableReason.InvalidEfforts, (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!.UnavailableReason);
        await using (var db = setup.Factory.CreateDbContext())
            await db.Activities.Where(activity => activity.Id == second).ExecuteUpdateAsync(update => update
                .SetProperty(activity => activity.Sport, SportKind.Running).SetProperty(activity => activity.OwnerId, other));
        Assert.Equal(SegmentComparisonUnavailableReason.InvalidEfforts, (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!.UnavailableReason);
        await using (var db = setup.Factory.CreateDbContext())
        {
            await db.Activities.Where(activity => activity.Id == second).ExecuteUpdateAsync(update => update.SetProperty(activity => activity.OwnerId, owner));
            await db.ActivityStreams.Where(stream => stream.ActivityId == second).ExecuteUpdateAsync(update => update.SetProperty(stream => stream.OwnerId, other));
        }
        Assert.Equal(SegmentComparisonUnavailableReason.MissingStream, (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!.UnavailableReason);
        await using (var db = setup.Factory.CreateDbContext())
            await db.SegmentEfforts.Where(effort => effort.Id == comparison.Id).ExecuteDeleteAsync();
        var deleted = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;
        Assert.Equal(SegmentComparisonUnavailableReason.InvalidEfforts, deleted.UnavailableReason);
        Assert.Null(deleted.Comparison);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetComparisonAsync(segment, baseline.Id, comparison.Id, cancelled.Token));
    }

    [Fact]
    public async Task Segment_comparison_pairs_repeated_child_placements_without_shifting_missing_passes_or_using_outsiders()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Repeated section athlete");
        var points = ComparisonLaps(4);
        var first = await setup.SeedActivityAsync(owner, "Four laps", SportKind.Running, points);
        var second = await setup.SeedActivityAsync(owner, "Four slower laps", SportKind.Running, ComparisonTiming(points, 2, 1));
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var segment = await service.CreateFromActivityAsync(new(owner, first, "Three lap route", 0, 240));
        var child = await service.CreateSubsegmentAsync(new(segment, "Repeated section", 20, 40));
        var detail = (await service.GetAsync(segment))!;
        var baseline = detail.Efforts.Single(effort => effort.ActivityId == first);
        var comparison = detail.Efforts.Single(effort => effort.ActivityId == second);
        var original = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;
        Assert.Equal(SegmentComparisonUnavailableReason.None, original.UnavailableReason);
        Assert.Equal(3, original.Children.Count);
        Assert.All(original.Children, row => Assert.Equal(SegmentChildComparisonStatus.Matched, row.ComparisonStatus));
        Assert.Equal(ThreeChildPassStarts, original.Children.Select(row => row.Comparison!.StartPointIndex));
        await using (var db = setup.Factory.CreateDbContext())
            await db.SegmentEfforts.Where(effort => effort.SegmentId == child && effort.ActivityId == second && effort.StartPointIndex == 100).ExecuteDeleteAsync();

        var missing = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;

        Assert.Equal(SegmentChildComparisonStatus.Missing, missing.Children[1].ComparisonStatus);
        Assert.Null(missing.Children[1].DeltaSeconds);
        Assert.Equal(180, missing.Children[2].Comparison!.StartPointIndex);
        Assert.All(missing.Children.Where(row => row.Comparison is not null), row => Assert.True(row.Comparison!.EndPointIndex <= comparison.EndPointIndex));
        await using (var db = setup.Factory.CreateDbContext())
        {
            db.SegmentEfforts.Add(new SegmentEffort
            {
                OwnerId = owner,
                SegmentId = child,
                ActivityId = second,
                StartPointIndex = 21,
                EndPointIndex = 41,
                StartTimeUtc = points[21].Timestamp!.Value.AddDays(1),
                ElapsedSeconds = 40,
                CoveragePercent = 100
            });
            await db.SaveChangesAsync();
        }
        var ambiguous = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;
        Assert.Equal(SegmentChildComparisonStatus.Ambiguous, ambiguous.Children[0].ComparisonStatus);
        Assert.Null(ambiguous.Children[0].Comparison);
        Assert.Null(ambiguous.Children[0].DeltaSeconds);
        Assert.Equal(SegmentChildComparisonStatus.Matched, ambiguous.Children[2].ComparisonStatus);
    }

    [Fact]
    public async Task Segment_comparison_keeps_original_source_positions_for_two_passes_in_one_activity()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Same recording athlete");
        var activity = await setup.SeedActivityAsync(owner, "Three recorded laps", SportKind.Running, ComparisonLaps(3));
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var segment = await service.CreateFromActivityAsync(new(owner, activity, "Single lap", 0, 80));
        await service.CreateSubsegmentAsync(new(segment, "Lap section", 20, 40));
        var detail = (await service.GetAsync(segment))!;
        var baseline = detail.Efforts.Single(effort => effort.StartPointIndex == 0);
        var comparison = detail.Efforts.Single(effort => effort.StartPointIndex == 160);

        var result = (await service.GetComparisonAsync(segment, baseline.Id, comparison.Id))!;

        Assert.Equal(SegmentComparisonUnavailableReason.None, result.UnavailableReason);
        Assert.Equal(0, result.Samples[0].Baseline.Source.Position);
        Assert.Equal(160, result.Samples[0].Comparison.Source.Position);
        Assert.Equal(80, result.Samples[^1].Baseline.Source.Position);
        Assert.Equal(240, result.Samples[^1].Comparison.Source.Position);
        var child = Assert.Single(result.Children);
        Assert.Equal(20, child.Baseline!.StartPointIndex);
        Assert.Equal(180, child.Comparison!.StartPointIndex);
        Assert.Equal(0, child.DeltaSeconds);
    }

    private static TrackPoint[] ComparisonTiming(IReadOnlyList<TrackPoint> points, int secondsPerPoint, int days) =>
        points.Select((point, index) => point with { Timestamp = points[0].Timestamp!.Value.AddDays(days).AddSeconds(index * secondsPerPoint) }).ToArray();

    private static TrackPoint[] ComparisonLaps(int laps)
    {
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, laps * 80 + 1).Select(index => new TrackPoint(start.AddSeconds(index),
            1 + 0.002 * Math.Sin(2 * Math.PI * index / 80), -30 + 0.002 * Math.Cos(2 * Math.PI * index / 80),
            null, 20 + 5 * Math.Sin(2 * Math.PI * index / 80), 4, 140, 80, 200, null)).ToArray();
    }
}

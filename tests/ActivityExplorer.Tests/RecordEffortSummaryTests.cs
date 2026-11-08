using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Tests;

public sealed class RecordEffortSummaryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SportKind.Cycling)]
    [InlineData(SportKind.Running)]
    [InlineData(SportKind.Walking)]
    [InlineData(SportKind.Rowing)]
    public void Summary_values_match_the_independent_multiple_attempt_winner_across_stream_eligibility(SportKind sport)
    {
        var canonical = Enumerable.Range(0, 54).Select(index => Point(Origin.AddSeconds(index), index * 3,
            index % 9 == 0 ? null : 100 + index % 7 * 50) with
        {
            Latitude = 55 + index * .00002,
            Longitude = 12 + index * .00001
        }).ToArray();
        var variants = new[]
        {
            canonical,
            canonical.Select(point => point with { Latitude = null, Longitude = null }).ToArray(),
            canonical.Select((point, index) => index % 7 == 0 ? point with { DistanceMeters = null } : point).ToArray(),
            canonical.Select((point, index) => index == 15 ? point with { DistanceMeters = double.NaN } : point).ToArray(),
            canonical.Select((point, index) => index == 15 ? point with { DistanceMeters = double.PositiveInfinity } : point).ToArray(),
            canonical.Select((point, index) => index is 18 or 19 ? point with { Latitude = 100 } : point).ToArray(),
            canonical.Select((point, index) => index >= 24 ? point with { DistanceMeters = point.DistanceMeters - 72 } : point).ToArray(),
            canonical.Select((point, index) => index >= 32 ? point with { Timestamp = point.Timestamp!.Value.AddSeconds(7) } : point).ToArray(),
            canonical.Select((point, index) => index is >= 10 and <= 14 ? point with { DistanceMeters = 30 } : point).ToArray(),
            canonical.Select((point, index) => index == 21 ? point with { Timestamp = null, DistanceMeters = double.NaN } : point).ToArray(),
            canonical.Select((point, index) => index == 25 ? point with { PowerWatts = double.NaN } : point).ToArray()
        };
        foreach (var points in variants)
            AssertMatchesBinaryWinners(points, sport);
    }

    [Fact]
    public void Nonfinite_recorded_distance_with_valid_GPS_keeps_timed_distance_fallback_separate()
    {
        TrackPoint[] points =
        [
            Point(Origin, 0, null),
            Point(Origin.AddSeconds(5), double.NaN, null) with { Latitude = 55.00002 },
            Point(Origin.AddSeconds(10), 50, null) with { Latitude = 55.00004 }
        ];
        var groups = Groups(SportKind.Cycling);
        var actual = BestEffortCalculator.BestValues(points, SportKind.Cycling, groups);
        Assert.DoesNotContain(actual, item => item.Benchmark.Kind == RecordKind.DistanceEffort);
        Assert.Contains(actual, item => item.Benchmark.Kind == RecordKind.TimedDistanceEffort);
        AssertMatchesBinaryWinners(points, SportKind.Cycling);
    }

    [Fact]
    public void Rounded_relative_timestamps_keep_the_binary_search_fallback_and_ties()
    {
        var origin = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = origin.AddYears(100);
        TrackPoint[] points =
        [
            Point(origin, 0, null), Point(later, 0, null), Point(later.AddTicks(1), .000001, null),
            Point(later.AddSeconds(5), 30, null), Point(later.AddSeconds(10), 30, null)
        ];
        AssertMatchesBinaryWinners(points, SportKind.Cycling);
    }

    [Fact]
    public void Empty_groups_invalid_targets_and_precancellation_keep_the_existing_contract()
    {
        var points = new[] { Point(Origin, 0, 200), Point(Origin.AddSeconds(4), 12, 200) };
        Assert.Empty(BestEffortCalculator.BestValues(points, SportKind.Cycling, []));
        RecordBenchmark[][] invalid =
        [
            [],
            [Benchmark(SportKind.Cycling, RecordKind.DistanceEffort, double.NaN), Benchmark(SportKind.Cycling, RecordKind.DistanceEffort, 0),
             Benchmark(SportKind.Cycling, RecordKind.DistanceEffort, double.PositiveInfinity)]
        ];
        Assert.Empty(BestEffortCalculator.BestValues(points, SportKind.Cycling, invalid));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BestEffortCalculator.BestValues(points, SportKind.Cycling, [], cancellation.Token));
    }

    [Fact]
    public void Summary_path_avoids_window_boundary_and_duplicate_distance_preparation_allocations()
    {
        var points = Enumerable.Range(0, 100).Select(index => Point(Origin.AddSeconds(index), index * 3, 200)).ToArray();
        var groups = Groups(SportKind.Cycling);
        var benchmarks = groups.SelectMany(group => group).ToArray();
        BestEffortCalculator.BestAttempts(points, SportKind.Cycling, benchmarks);
        BestEffortCalculator.BestValues(points, SportKind.Cycling, groups);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var repetition = 0; repetition < 32; repetition++)
            GC.KeepAlive(BestEffortCalculator.BestAttempts(points, SportKind.Cycling, benchmarks));
        var materialized = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var repetition = 0; repetition < 32; repetition++)
            GC.KeepAlive(BestEffortCalculator.BestValues(points, SportKind.Cycling, groups));
        var summaries = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(summaries < materialized, $"Summary path allocated {summaries:N0} bytes versus {materialized:N0} bytes for windows.");
    }

    private static void AssertMatchesBinaryWinners(IReadOnlyList<TrackPoint> points, SportKind sport)
    {
        var groups = Groups(sport);
        foreach (var order in new[] { groups, groups.Reverse().ToArray() })
        {
            var actual = BestEffortCalculator.BestValues(points, sport, order).ToDictionary(item => item.Benchmark, item => item.Value);
            foreach (var benchmark in groups.SelectMany(group => group))
            {
                // Multiple attempts retains the binary-search candidate path, independent of the single-winner cursor.
                var expected = BestEffortCalculator.Attempts(points, sport, benchmark.Kind, benchmark.Target!.Value, multiple: true);
                Assert.Equal(expected.Count > 0, actual.TryGetValue(benchmark, out var summary));
                if (expected.Count == 0) continue;
                var winner = expected[0];
                Assert.Equal(new EffortValue(winner.Value, winner.CoveragePercent, winner.StartPosition, winner.FinishPosition), summary);
            }
        }
    }

    private static RecordBenchmark[][] Groups(SportKind sport) =>
    [
        new[] { 1d, 17.25, 5_000 }.Select(target => Benchmark(sport, RecordKind.DistanceEffort, target)).ToArray(),
        new[] { 1.25, 4.75, 80 }.Select(target => Benchmark(sport, RecordKind.TimedDistanceEffort, target)).ToArray(),
        new[] { 4.75, 5, 100 }.Select(target => Benchmark(sport, RecordKind.PowerCurve, target)).ToArray()
    ];

    private static RecordBenchmark Benchmark(SportKind sport, RecordKind kind, double target) => new(sport, kind, $"{kind}-{target}", "Summary parity", target);
    private static TrackPoint Point(DateTimeOffset time, double distance, double? power) => new(time, 55, 12, distance, null, null, null, null, power, null);
}

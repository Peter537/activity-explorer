using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;

namespace ActivityExplorer.Tests;

public sealed class RecordEffortCursorTests
{
    [Fact]
    public void Distinct_timestamps_that_round_to_one_relative_second_keep_binary_search_ties()
    {
        var origin = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = origin.AddYears(100);
        Assert.Equal((later - origin).TotalSeconds, (later.AddTicks(1) - origin).TotalSeconds);
        var points = new[]
        {
            Point(origin, 0, null), Point(later, 0, null), Point(later.AddTicks(1), 0.000001, null),
            Point(later.AddSeconds(5), 50, null), Point(later.AddSeconds(10), 50, null)
        };
        AssertBestEqualsFirstMultiple(points, SportKind.Cycling, RecordKind.TimedDistanceEffort, 5);
    }

    [Theory]
    [InlineData(SportKind.Cycling, RecordKind.DistanceEffort, 17.25)]
    [InlineData(SportKind.Rowing, RecordKind.DistanceEffort, 17.25)]
    [InlineData(SportKind.Running, RecordKind.TimedDistanceEffort, 4.75)]
    [InlineData(SportKind.Cycling, RecordKind.PowerCurve, 4.75)]
    public void Fractional_targets_match_binary_search_across_plateaus_and_segment_breaks(
        SportKind sport, RecordKind kind, double target)
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var points = new List<TrackPoint>();
        var random = new Random(8721);
        double distance = 0;
        for (var i = 0; i < 54; i++)
        {
            time = time.AddTicks(i == 32 ? 7 * TimeSpan.TicksPerSecond : random.Next(10_000_000, 18_000_000));
            if (i == 24) distance = 0;
            else if (i % 7 >= 3) distance += random.Next(1, 70) / 8.0;
            var point = Point(time, distance, i % 9 == 0 ? null : random.Next(1, 6) * 50);
            points.Add(i is 18 or 19 ? point with { Latitude = null, Longitude = null } : point);
        }

        foreach (var threshold in new[] { Math.BitDecrement(target), target, Math.BitIncrement(target) })
            AssertBestEqualsFirstMultiple(points, sport, kind, threshold);
    }

    private static void AssertBestEqualsFirstMultiple(IReadOnlyList<TrackPoint> points, SportKind sport,
        RecordKind kind, double target)
    {
        // The multiple-attempt path still uses binary search to evaluate source anchors.
        var expected = BestEffortCalculator.Attempts(points, sport, kind, target, multiple: true);
        Assert.NotEmpty(expected);
        var best = Assert.Single(BestEffortCalculator.Attempts(points, sport, kind, target, multiple: false));
        Assert.Equal(expected[0], best);
    }

    private static TrackPoint Point(DateTimeOffset time, double distance, double? power) =>
        new(time, 55, 12, distance, null, null, null, null, power, null);
}

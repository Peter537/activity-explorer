using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Tests;

public sealed class SegmentFindingTests
{
    [Fact]
    public void Matching_shared_descendant_keeps_every_ancestor_path_and_excludes_unrelated_branches()
    {
        var firstRoot = Segment("Coast loop");
        var secondRoot = Segment("Forest loop");
        var middle = Segment("Inner loop");
        var shared = Segment("Søen ØST");
        var unrelated = Segment("Unrelated section");
        var otherSport = Segment("Søen øst walk", SportKind.Walking);
        var matches = SegmentLibraryFinding.Find([firstRoot, secondRoot, middle, shared, unrelated, otherSport],
            [new(firstRoot.Id, middle.Id), new(middle.Id, shared.Id), new(secondRoot.Id, shared.Id),
             new(firstRoot.Id, unrelated.Id), new(Guid.NewGuid(), shared.Id)], " øSt ", SportKind.Running);
        Assert.Equal([shared.Id], matches.MatchingIds);
        Assert.Equal(new[] { firstRoot.Id, secondRoot.Id, middle.Id, shared.Id }.Order(), matches.VisibleIds.Order());
        Assert.Empty(SegmentLibraryFinding.Find([shared], [], "absent", null).VisibleIds);
    }

    [Fact]
    public void Effort_filters_use_inclusive_owner_local_dates_across_a_daylight_saving_boundary()
    {
        var start = Effort("2026-03-28T23:00:00Z", 15.25, rank: 7);
        var end = Effort("2026-03-29T21:59:59Z", 60.75, rank: 2);
        var before = Effort("2026-03-28T22:59:59Z", 30);
        var after = Effort("2026-03-29T22:00:00Z", 30);
        var shortPass = Effort("2026-03-29T12:00:00Z", 15.24999);
        var longPass = Effort("2026-03-29T12:00:00Z", 60.75001);
        var filter = new SegmentEffortFilter(new(2026, 3, 29), new(2026, 3, 29), 15.25, 60.75);
        var result = SegmentEffortFinding.Apply([before, start, end, after, shortPass, longPass], filter, "Europe/Copenhagen");
        Assert.Equal(new[] { end.Id, start.Id }, result.Select(x => x.Id));
        Assert.Equal([2, 7], result.Select(x => x.Rank));
        Assert.Equal(new[] { start.Id, end.Id }, SegmentEffortFinding.Apply([start, end], filter with { Sort = "elapsed" },
            "Europe/Copenhagen").Select(x => x.Id));
    }

    [Fact]
    public void Effort_sorting_retains_separate_repeated_passes_and_deterministic_ties()
    {
        var activity = Guid.NewGuid();
        var laterPass = Effort("2026-01-01T12:00:00Z", 45, 10, 9, activity);
        var earlierPass = Effort("2026-01-01T12:00:00Z", 45, 1, 3, activity);
        var noSensor = Effort("2026-01-01T12:00:01Z", 46) with { AveragePower = null };
        laterPass = laterPass with { AveragePower = 0 };
        earlierPass = earlierPass with { AveragePower = 0 };
        foreach (var sort in new[] { "elapsed", "speed", "heart-rate", "power" })
        {
            var ordered = SegmentEffortFinding.Apply([laterPass, noSensor, earlierPass], new(Sort: sort), "UTC");
            Assert.Equal(new[] { earlierPass.Id, laterPass.Id, noSensor.Id }, ordered.Select(x => x.Id));
            Assert.Equal([3, 9], ordered.Take(2).Select(x => x.Rank));
        }
    }

    [Theory]
    [InlineData(-1d, null, "rank")]
    [InlineData(double.NaN, null, "rank")]
    [InlineData(null, double.PositiveInfinity, "rank")]
    [InlineData(61d, 60d, "rank")]
    [InlineData(null, null, "unknown")]
    public void Invalid_effort_duration_or_sort_is_rejected(double? minimum, double? maximum, string sort) =>
        Assert.Throws<ArgumentException>(() => SegmentEffortFinding.Apply([], new(MinimumElapsedSeconds: minimum,
            MaximumElapsedSeconds: maximum, Sort: sort), "UTC"));

    [Fact]
    public void Reversed_effort_dates_are_rejected() => Assert.Throws<ArgumentException>(() =>
        SegmentEffortFinding.Apply([], new(new(2026, 10, 2), new(2026, 10, 1)), "UTC"));

    private static SegmentSummary Segment(string name, SportKind sport = SportKind.Running) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Athlete", name, sport, 1000, 30, 0, null, null, null, null);

    private static SegmentEffortSummary Effort(string instant, double elapsed, int start = 0, int rank = 1, Guid? activity = null) =>
        new(Guid.NewGuid(), activity ?? Guid.NewGuid(), Guid.Empty, "Section", elapsed, rank,
            DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture), start, start + 1, elapsed,
            2, null, null, null, null, null, null, null, null, null, null, null, null, null, 100, true);
}

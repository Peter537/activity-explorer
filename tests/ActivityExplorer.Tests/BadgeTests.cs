using System.Diagnostics;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed class BadgeTests
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly int[] WeeklyDays = [0, 2, 4];
    private static readonly int[] PointTiers = [1, 2, 4, 8, 16];

    [Fact]
    public void Historical_imports_stack_tiers_once_and_ignore_input_order_and_duplicate_ids()
    {
        var early = Activity("2018-04-02T10:00:00Z", distance: 250000);
        var late = Activity("2018-04-20T10:00:00Z", distance: 150000);
        var laterYear = Activity("2026-04-20T10:00:00Z", distance: 400000);
        var snapshot = Evaluate([late, early, early, laterYear], new(2018, 4, 1));
        var tiers = snapshot.ForMonth.Where(x => x.Definition.FamilyId == "cycling-monthly-distance" && x.Status == BadgeStatus.Completed).ToArray();
        Assert.Equal(7, tiers.Sum(x => x.Definition.Points));
        Assert.Equal(new DateOnly(2018, 4, 20), Badge(snapshot, "cycling-monthly-distance-400", "2018-04").EarnedOn);
        Assert.Equal(new DateOnly(2018, 4, 2), Badge(snapshot, "cycling-lifetime-singledistance-200").EarnedOn);
        var reordered = Evaluate([early, laterYear, late], new(2018, 4, 1));
        Assert.Equal(snapshot.Level, reordered.Level);
        Assert.Equal(snapshot.Editions.Select(Key), reordered.Editions.Select(Key));
        Assert.All(snapshot.Editions.Where(x => x.EarnedOn.HasValue), x => Assert.True(x.EarnedOn <= snapshot.AsOf));
        Assert.Equal(snapshot.EarnedCount, snapshot.Editions.Where(x => x.Status == BadgeStatus.Completed).Select(x => x.Identity(Owner)).Distinct().Count());
    }

    [Fact]
    public void Month_snapshots_include_overlapping_periods_and_never_later_activity()
    {
        var april = Activity("2026-04-10T10:00:00Z", distance: 100000);
        var may = Activity("2026-05-01T10:00:00Z", distance: 1000000);
        var future = Activity("2026-12-31T10:00:00Z", distance: 1000000);
        var snapshot = Evaluate([april, may, future], new(2026, 4, 1));
        Assert.Equal(100, Badge(snapshot, "cycling-quarterly-distance-1000", "2026-Q2").Progress);
        Assert.Equal(BadgeStatus.InProgress, Badge(snapshot, "cycling-quarterly-distance-1000", "2026-Q2").Status);
        Assert.Equal(BadgeStatus.Incomplete, Badge(snapshot, "cycling-monthly-distance-200", "2026-04").Status);
        Assert.Equal(BadgeStatus.NotStarted, Badge(snapshot, "running-monthly-distance-20", "2026-04").Status);
        Assert.Contains(snapshot.ForMonth, x => x.Definition.Id == "earth-day");
        Assert.DoesNotContain(snapshot.ForMonth, x => x.Definition.Id == "bicycle-day");
        Assert.Contains(snapshot.ForMonth, x => x.Edition == "2026-Q2");
        Assert.Contains(snapshot.ForMonth, x => x.Definition.Id == "cycling-annual-distance-4000");
        var preview = Evaluate([april, may, future], new(2026, 12, 1));
        Assert.Equal(new DateOnly(2026, 9, 7), preview.AsOf);
        Assert.Equal(BadgeStatus.Upcoming, Badge(preview, "year-end", "2026").Status);
        Assert.Equal(0, Badge(preview, "cycling-monthly-distance-100", "2026-12").Progress);
    }

    [Theory]
    [InlineData("2026-01-01T02:59:00Z", false, true)]
    [InlineData("2026-01-01T03:00:00Z", true, false)]
    [InlineData("2026-01-01T05:59:00Z", true, false)]
    [InlineData("2026-01-01T06:00:00Z", false, false)]
    [InlineData("2026-01-01T20:59:00Z", false, false)]
    [InlineData("2026-01-01T21:00:00Z", false, true)]
    [InlineData("2018-07-01T02:00:00Z", true, false)]
    [InlineData("2018-10-28T02:00:00Z", false, true)]
    public void Clock_windows_use_profile_timezone_and_historical_dst(string start, bool morning, bool night)
    {
        var activity = Activity(start, moving: 600);
        var snapshot = Evaluate([activity]);
        var year = activity.StartTimeUtc.Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(morning, Badge(snapshot, "all-annual-morning-1", year).EarnedOn.HasValue);
        Assert.Equal(night, Badge(snapshot, "all-annual-night-1", year).EarnedOn.HasValue);
    }

    [Fact]
    public void Calendar_days_use_start_date_and_separate_yearly_awards()
    {
        var snapshot = Evaluate([
            Activity("2024-12-31T22:59:00Z"), Activity("2025-12-31T22:59:00Z"),
            Activity("2025-12-31T23:01:00Z"), Activity("2024-02-29T10:00:00Z"),
            Activity("2026-06-03T10:00:00Z", SportKind.Walking), Activity("2026-06-05T10:00:00Z")]);
        Assert.Equal(2, snapshot.Editions.Count(x => x.Definition.Id == "year-end" && x.EarnedOn.HasValue));
        Assert.Equal(new DateOnly(2026, 1, 1), Badge(snapshot, "new-year", "2026").EarnedOn);
        Assert.Equal(BadgeStatus.Completed, Badge(snapshot, "leap-day", "2024").Status);
        Assert.DoesNotContain(snapshot.Editions, x => x.Definition.Id == "leap-day" && x.Edition == "2025");
        Assert.Equal(BadgeStatus.NotStarted, Badge(snapshot, "bicycle-day", "2026").Status);
        Assert.Equal(BadgeStatus.Completed, Badge(snapshot, "environment-day", "2026").Status);
    }

    [Fact]
    public void Missing_metrics_do_not_become_progress_and_duration_gates_are_exact()
    {
        var invalid = Activity("2026-01-01T03:00:00Z", distance: double.NaN, moving: 3600, ascent: double.PositiveInfinity)
            with
        { MovingTimeSource = MovingTimeSource.Unavailable };
        var shortActivity = Activity("2026-01-02T03:00:00Z", distance: 4999.999, moving: 599.999, ascent: -1);
        var snapshot = Evaluate([invalid, shortActivity]);
        Assert.Equal(BadgeStatus.NotStarted, Badge(snapshot, "all-annual-morning-1", "2026").Status);
        Assert.Equal(0, Badge(snapshot, "cycling-lifetime-activitycount-1").Progress);
        Assert.Null(Badge(snapshot, "cycling-lifetime-singledistance-5").EarnedOn);
        Assert.Equal(0, Badge(snapshot, "cycling-lifetime-singleascent-100").Progress);
        var exact = Evaluate([Activity("2026-01-01T03:00:00Z", distance: 5000, moving: 600, ascent: 100)]);
        Assert.NotNull(Badge(exact, "cycling-lifetime-singledistance-5").EarnedOn);
        Assert.NotNull(Badge(exact, "cycling-lifetime-singleascent-100").EarnedOn);
        Assert.NotNull(Badge(exact, "cycling-lifetime-activitycount-1").EarnedOn);
        Assert.NotNull(Badge(exact, "all-annual-morning-1", "2026").EarnedOn);
    }

    [Fact]
    public void Active_days_sum_short_activities_and_variety_requires_qualifying_sports()
    {
        var activities = new List<BadgeActivity>();
        for (var day = 1; day <= 5; day++)
        {
            activities.Add(Activity($"2026-04-{day:00}T10:00:00Z", moving: 600));
            activities.Add(Activity($"2026-04-{day:00}T11:00:00Z", moving: 600));
        }
        activities.Add(Activity("2026-04-06T10:00:00Z", SportKind.Running, moving: 600));
        activities.Add(Activity("2026-04-07T10:00:00Z", SportKind.Walking, moving: 600));
        activities.Add(Activity("2026-04-08T10:00:00Z", SportKind.Rowing, moving: 599));
        var snapshot = Evaluate(activities, new(2026, 4, 1));
        Assert.Equal(new DateOnly(2026, 4, 5), Badge(snapshot, "all-monthly-activedays-5", "2026-04").EarnedOn);
        Assert.Equal(10, Badge(snapshot, "all-monthly-activedays-5", "2026-04").Evidence.Count);
        Assert.Equal(3, Badge(snapshot, "all-monthly-variety-4", "2026-04").Progress);
        Assert.Equal(2, Badge(snapshot, "all-monthly-variety-3", "2026-04").Definition.Points);
    }

    [Fact]
    public void Weekly_consistency_crosses_years_and_awards_on_third_active_day()
    {
        var monday = new DateTimeOffset(2025, 12, 15, 12, 0, 0, TimeSpan.Zero);
        var activities = Enumerable.Range(0, 4).SelectMany(week => WeeklyDays.Select(day =>
            Activity(monday.AddDays(week * 7 + day).ToString("O"), moving: 1200))).ToList();
        var snapshot = Evaluate(activities);
        var badge = Badge(snapshot, "all-lifetime-consistentweeks-4");
        Assert.Equal(new DateOnly(2026, 1, 9), badge.EarnedOn);
        Assert.Equal(12, badge.Evidence.Count);
        activities.RemoveRange(3, 3);
        var broken = Badge(Evaluate(activities), "all-lifetime-consistentweeks-4");
        Assert.Equal(2, broken.Progress);
        Assert.Null(broken.EarnedOn);
    }

    [Fact]
    public void Fractional_display_units_accumulate_in_recorded_units_at_exact_thresholds()
    {
        var activities = Enumerable.Range(0, 30).Select(i => Activity(
            new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero).AddDays(i).ToString("O"), moving: 600)).ToArray();
        var snapshot = Evaluate(activities, new(2026, 4, 1));
        Assert.Equal(BadgeStatus.Completed, Badge(snapshot, "cycling-monthly-movingtime-5", "2026-04").Status);
        Assert.Equal(new DateOnly(2026, 4, 30), Badge(snapshot, "cycling-monthly-movingtime-5", "2026-04").EarnedOn);
        var shortByOneSecond = activities.ToArray();
        shortByOneSecond[^1] = shortByOneSecond[^1] with { MovingSeconds = 599 };
        Assert.Null(Badge(Evaluate(shortByOneSecond, new(2026, 4, 1)), "cycling-monthly-movingtime-5", "2026-04").EarnedOn);
    }

    [Fact]
    public void Empty_history_default_timezone_invalid_timezone_and_cancellation_are_explicit()
    {
        var snapshot = Evaluate([]);
        Assert.Equal(BadgeTimeZone.DefaultId, snapshot.TimeZoneId);
        Assert.Equal(1, snapshot.Level.Level);
        Assert.NotEmpty(snapshot.ForMonth);
        Assert.Equal(BadgeCatalog.Definitions.Select(x => x.FamilyId).Distinct().Count(), snapshot.Editions.Select(x => x.Definition.FamilyId).Distinct().Count());
        Assert.Equal(BadgeStatus.Upcoming, Badge(snapshot, "leap-day", "2028").Status);
        Assert.Equal(0, snapshot.EarnedCount);
        Assert.Equal(new DateOnly(2026, 1, 1), Evaluate([], new(2018, 1, 1)).Month);
        Assert.Throws<InvalidOperationException>(() => BadgeEvaluator.Evaluate(Owner, "Example", "Missing/Zone", [], null, Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BadgeEvaluator.Evaluate(Owner, "Example", null, [], null, Now, cancellation.Token));
    }

    [Fact]
    public void All_catalogue_targets_qualify_exactly_and_definitions_have_unique_identities()
    {
        Assert.Equal(BadgeCatalog.Definitions.Count, BadgeCatalog.Definitions.Select(x => x.Id).Distinct().Count());
        Assert.All(BadgeCatalog.Definitions, x => Assert.Contains(x.Points, PointTiers));
        foreach (var definition in BadgeCatalog.Definitions.Where(x => x.Measure is BadgeMeasure.SingleDistance or BadgeMeasure.SingleAscent))
        {
            var activity = Activity("2026-04-01T10:00:00Z", definition.Sport!.Value,
                distance: definition.Unit == "km" ? definition.Target * 1000 : 0,
                ascent: definition.Unit == "m" ? definition.Target : 0);
            Assert.Equal(BadgeStatus.Completed, Badge(Evaluate([activity]), definition.Id).Status);
        }
    }

    [Fact]
    public void Level_boundaries_are_progressive_and_points_are_never_spent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BadgeLevel.FromPoints(-1));
        for (var level = 2; level <= 100; level++)
        {
            var threshold = 5L * (level - 1) * (level - 1);
            Assert.Equal(level - 1, BadgeLevel.FromPoints(threshold - 1).Level);
            Assert.Equal(level, BadgeLevel.FromPoints(threshold).Level);
            var above = BadgeLevel.FromPoints(threshold + 1);
            Assert.Equal(level, above.Level);
            Assert.Equal(1, above.EarnedInLevel);
            Assert.Equal(10 * level - 5, above.RequiredInLevel);
        }
    }

    [Theory]
    [InlineData(SportKind.Cycling, 1, 25, 1.25, 150, 28)]
    [InlineData(SportKind.Cycling, 3, 50, 2, 400, 260)]
    [InlineData(SportKind.Cycling, 5, 50, 2, 400, 408)]
    [InlineData(SportKind.Walking, 4, 5, 1, 50, 192)]
    [InlineData(SportKind.Rowing, 3, 5, .5, 0, 156)]
    public void Fictional_full_year_schedules_reproduce_the_approved_balance(SportKind sport, int frequency, double km, double hours, double ascent, int expectedPoints)
    {
        var days = frequency switch { 1 => new[] { 6 }, 3 => [2, 4, 6], 4 => [1, 3, 5, 0], _ => [1, 2, 4, 6, 0] };
        var activities = Enumerable.Range(0, 365).Select(day => new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(day))
            .Where(date => days.Contains((int)date.DayOfWeek)).Select(date => Activity(date.ToString("O"), sport, km * 1000, hours * 3600, ascent)).ToArray();
        var snapshot = Evaluate(activities, new(2025, 12, 1));
        var points = snapshot.Editions.Where(x => x.Status == BadgeStatus.Completed &&
            (x.Definition.Period is BadgePeriod.Monthly or BadgePeriod.Quarterly ||
             x.Definition.Period == BadgePeriod.Annual && x.Definition.Measure == BadgeMeasure.Distance)).Sum(x => x.Definition.Points);
        Assert.Equal(expectedPoints, points);
    }

    [Fact]
    public void Documentation_matches_every_catalogue_family_target_and_point_tier()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ActivityExplorer.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var document = File.ReadAllText(Path.Combine(directory.FullName, "docs", "badges.md"));
        var families = BadgeCatalog.Definitions.GroupBy(x => x.FamilyId).ToArray();
        Assert.Contains($"Catalogue version **{BadgeCatalog.Version}** has **{BadgeCatalog.Definitions.Count} definitions in {families.Length} families**", document, StringComparison.Ordinal);
        foreach (var family in families)
        {
            var targets = string.Join(" / ", family.Select(x => x.Target.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
            var points = string.Join(" / ", family.Select(x => x.Points));
            Assert.Contains($"| {family.Key} | {targets} | {family.First().Unit} | {points} |", document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ten_thousand_summaries_evaluate_without_streams()
    {
        var start = new DateTimeOffset(2010, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var activities = Enumerable.Range(0, 10000).Select(i => Activity(start.AddHours(i * 12).ToString("O"), (SportKind)(i % 4 + 1), 20000, 3600, 200)).ToArray();
        Evaluate(activities);
        var timer = Stopwatch.StartNew();
        var result = Evaluate(activities);
        timer.Stop();
        Assert.True(result.EarnedCount > 100);
        TestContext.Current.TestOutputHelper!.WriteLine($"10,000-summary evaluator: {timer.Elapsed.TotalMilliseconds:N1} ms");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "Evaluation exceeded the generous regression ceiling; the local target is one second.");
    }

    internal static BadgeActivity Activity(string start, SportKind sport = SportKind.Cycling, double distance = 10000, double moving = 1200, double ascent = 0) =>
        new(Guid.NewGuid(), sport, DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture), "Fictional training", distance, moving, MovingTimeSource.SourceSummary, ascent);
    private static BadgeSnapshot Evaluate(IReadOnlyList<BadgeActivity> activities, DateOnly? month = null) => BadgeEvaluator.Evaluate(Owner, "Example athlete", null, activities, month, Now);
    internal static BadgeEdition Badge(BadgeSnapshot snapshot, string id, string edition = "lifetime") => Assert.Single(snapshot.Editions, x => x.Definition.Id == id && x.Edition == edition);
    private static object Key(BadgeEdition edition) => new { edition.Definition.Id, edition.Edition, edition.Status, edition.Progress, edition.EarnedOn };
}

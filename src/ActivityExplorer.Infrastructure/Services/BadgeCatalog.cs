using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Infrastructure.Services;

public static class BadgeCatalog
{
    public const int Version = 1;
    public static IReadOnlyList<BadgeDefinition> Definitions { get; } = Build().AsReadOnly();

    private static List<BadgeDefinition> Build()
    {
        var result = new List<BadgeDefinition>();
        foreach (var sport in Enum.GetValues<SportKind>())
        {
            Add(sport, BadgePeriod.Monthly, BadgeMeasure.Distance, "Monthly distance", sport switch
            {
                SportKind.Cycling => [100, 200, 400, 800],
                SportKind.Running => [20, 50, 100, 200],
                _ => [10, 25, 50, 100]
            }, [1, 2, 4, 8], "km", "distance");
            Add(sport, BadgePeriod.Quarterly, BadgeMeasure.Distance, "Quarterly distance", [sport == SportKind.Cycling ? 1000 : sport == SportKind.Running ? 250 : 150], [4], "km", "distance");
            Add(sport, BadgePeriod.Annual, BadgeMeasure.Distance, "Annual distance", [sport == SportKind.Cycling ? 4000 : sport == SportKind.Running ? 1000 : 600], [8], "km", "distance");
            Add(sport, BadgePeriod.Lifetime, BadgeMeasure.Distance, "Lifetime distance", sport == SportKind.Cycling ? [1000, 5000, 10000, 25000] : [100, 500, 1000, 5000], [2, 4, 8, 16], "km", "distance");
            var singleTargets = sport switch
            {
                SportKind.Cycling => new double[] { 5, 10, 25, 50, 100, 150, 200 },
                SportKind.Running => [1, 5, 10, 21.0975, 42.195],
                SportKind.Walking => [1, 5, 10, 20, 30, 50],
                _ => [1, 2, 5, 10, 21.0975, 42.195]
            };
            Add(sport, BadgePeriod.Lifetime, BadgeMeasure.SingleDistance, "Single-activity distance", singleTargets,
                sport == SportKind.Cycling ? [1, 1, 2, 4, 8, 8, 16] : sport == SportKind.Running ? [1, 2, 4, 8, 16] : [1, 1, 2, 4, 8, 16], "km", "distance");
            Add(sport, BadgePeriod.Monthly, BadgeMeasure.MovingTime, "Monthly moving time", [5, 10, 20], [1, 2, 4], "hours", "clock");
            Add(sport, BadgePeriod.Lifetime, BadgeMeasure.ActivityCount, "Lifetime activities", [1, 25, 100, 500, 1000], [1, 2, 4, 8, 16], "activities", "count");
            if (sport != SportKind.Rowing)
            {
                Add(sport, BadgePeriod.Monthly, BadgeMeasure.Ascent, "Monthly ascent", sport == SportKind.Cycling ? [1000, 5000, 10000] : [250, 1000, 2000], [1, 2, 4], "m", "mountain");
                Add(sport, BadgePeriod.Lifetime, BadgeMeasure.SingleAscent, "Single-activity ascent", sport == SportKind.Cycling ? [100, 250, 500, 1000, 2000] : [100, 250, 500, 1000], sport == SportKind.Cycling ? [1, 2, 4, 8, 16] : [1, 2, 4, 8], "m", "mountain");
            }
        }
        Add(null, BadgePeriod.Monthly, BadgeMeasure.ActiveDays, "Active days", [5, 10, 20], [1, 2, 4], "days", "calendar");
        Add(null, BadgePeriod.Monthly, BadgeMeasure.Variety, "Sport explorer", [3, 4], [2, 4], "sports", "variety");
        Add(null, BadgePeriod.Lifetime, BadgeMeasure.ConsistentWeeks, "Steady rhythm", [4, 12, 26], [2, 4, 8], "weeks", "calendar");
        Add(null, BadgePeriod.Annual, BadgeMeasure.Morning, "Early Bird", [1], [1], "activity", "sun");
        Add(null, BadgePeriod.Annual, BadgeMeasure.Night, "Night Owl", [1], [1], "activity", "moon");
        Date("new-year", "New Year's Day", 1, 1, "fireworks");
        Date("year-end", "New Year's Eve", 12, 31, "fireworks");
        Date("leap-day", "Leap Day", 2, 29, "calendar");
        Date("earth-day", "Earth Day", 4, 22, "leaf");
        Date("bicycle-day", "World Bicycle Day", 6, 3, "distance", SportKind.Cycling);
        Date("environment-day", "World Environment Day", 6, 5, "leaf");
        return result;

        void Add(SportKind? sport, BadgePeriod period, BadgeMeasure measure, string familyName, double[] targets, int[] points, string unit, string art)
        {
            var family = $"{sport?.ToString().ToLowerInvariant() ?? "all"}-{period.ToString().ToLowerInvariant()}-{measure.ToString().ToLowerInvariant()}";
            for (var i = 0; i < targets.Length; i++)
            {
                var amount = targets[i].ToString("0.####", CultureInfo.InvariantCulture);
                var displayUnit = targets[i] == 1 && unit == "activities" ? "activity" : unit;
                var name = measure is BadgeMeasure.Morning or BadgeMeasure.Night ? familyName : $"{familyName} · {amount} {displayUnit}";
                var requirement = measure switch
                {
                    BadgeMeasure.SingleDistance => $"Record at least {amount} km in one {sport!.Value.ToString().ToLowerInvariant()} activity.",
                    BadgeMeasure.SingleAscent => $"Climb at least {amount} m in one {sport!.Value.ToString().ToLowerInvariant()} activity.",
                    BadgeMeasure.ActiveDays => $"Accumulate at least 20 moving minutes on each of {amount} different days this month.",
                    BadgeMeasure.Variety => $"Record a 10-minute moving activity in each of {amount} different sports this month.",
                    BadgeMeasure.ConsistentWeeks => $"Complete {amount} consecutive Monday–Sunday weeks with at least three active days per week. Each active day needs 20 total moving minutes.",
                    BadgeMeasure.Morning => "Start an activity at or after 04:00 and before 07:00, with at least 10 moving minutes, this year.",
                    BadgeMeasure.Night => "Start an activity at or after 22:00 or before 04:00, with at least 10 moving minutes, this year.",
                    BadgeMeasure.ActivityCount => $"Record {amount} {sport!.Value.ToString().ToLowerInvariant()} {displayUnit} with at least 10 moving minutes each, across your history.",
                    _ => $"Accumulate {amount} {unit} of {sport!.Value.ToString().ToLowerInvariant()} {(measure == BadgeMeasure.Ascent ? "ascent" : measure == BadgeMeasure.MovingTime ? "moving time" : "distance")} {PeriodWords(period)}."
                };
                result.Add(new($"{family}-{amount.Replace('.', '-')}", family, name, familyName, sport, period, measure, targets[i], unit, points[i], art, requirement));
            }
        }

        void Date(string id, string name, int month, int day, string art, SportKind? sport = null) => result.Add(new(
            id, id, name, name, sport, BadgePeriod.SpecialDate, BadgeMeasure.SpecialDate, 1, "activity", 1, art,
            $"Start {(sport.HasValue ? "a cycling" : "an")} activity on {new DateOnly(2024, month, day).ToString("MMMM d", CultureInfo.InvariantCulture)}, with at least 10 moving minutes.", month, day));
    }

    private static string PeriodWords(BadgePeriod period) => period switch
    {
        BadgePeriod.Monthly => "this calendar month",
        BadgePeriod.Quarterly => "this calendar quarter",
        BadgePeriod.Annual => "this calendar year",
        _ => "across your history"
    };
}

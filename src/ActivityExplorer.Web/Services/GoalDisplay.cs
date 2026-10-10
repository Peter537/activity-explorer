using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

internal static class GoalDisplay
{
    public static string Label(GoalMetric metric) => metric switch
    {
        GoalMetric.MovingTime => "Moving time",
        GoalMetric.ActivityCount => "Activities",
        GoalMetric.ActiveDays => "Active days",
        _ => metric.ToString()
    };
    public static string Unit(GoalMetric metric) => metric switch
    {
        GoalMetric.Distance => "km",
        GoalMetric.MovingTime => "hours",
        GoalMetric.Ascent => "m",
        GoalMetric.ActivityCount => "activities",
        _ => "days"
    };
    public static double Scale(GoalMetric metric) => metric switch
    {
        GoalMetric.Distance => 1000,
        GoalMetric.MovingTime => 3600,
        _ => 1
    };
    public static string Amount(GoalMetric metric, double amount)
    {
        var value = amount / Scale(metric);
        var unit = value == 1 ? metric switch
        {
            GoalMetric.MovingTime => "hour",
            GoalMetric.ActivityCount => "activity",
            GoalMetric.ActiveDays => "day",
            _ => Unit(metric)
        } : Unit(metric);
        return $"{Number(value)} {unit}";
    }
    public static string Number(double value)
    {
        if (!double.IsFinite(value)) return "Unavailable";
        if (Math.Abs(value) is > 0 and < .01 or >= 1_000_000_000) return value.ToString("G5", CultureInfo.CurrentCulture);
        return value.ToString("N2", CultureInfo.CurrentCulture).TrimEnd('0').TrimEnd(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.ToCharArray());
    }
    public static string Range(GoalEdition edition) => $"{edition.Start:dd MMM yyyy} – {edition.End:dd MMM yyyy}";
    public static string Recurrence(GoalRecurrence recurrence) => recurrence == GoalRecurrence.Once ? "One-off" : recurrence.ToString();
    public static string Url(Guid id, DateOnly? edition = null, int historyPage = 1, int contributionPage = 1)
        => $"/goals/{id}?historyPage={historyPage}&contributionPage={contributionPage}" + (edition.HasValue ? $"&edition={edition.Value:yyyy-MM-dd}" : "");
}

public sealed class GoalFormValues
{
    public string Name { get; set; } = "";
    public string Sport { get; set; } = "";
    public string Target { get; set; } = "";

    public static GoalFormValues From(GoalMetric metric, GoalDefinition definition) => new()
    {
        Name = definition.Name,
        Sport = definition.Sport?.ToString() ?? "",
        Target = (definition.Target / GoalDisplay.Scale(metric)).ToString("R", CultureInfo.InvariantCulture)
    };

    public GoalDefinition Definition(GoalMetric metric)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 120) throw new ArgumentException("Enter a goal name of 1–120 characters.");
        if (!double.TryParse(Target, NumberStyles.Float, CultureInfo.InvariantCulture, out var target) ||
            !double.IsFinite(target) || target <= 0 || !double.IsFinite(target * GoalDisplay.Scale(metric)))
            throw new ArgumentException("Enter a finite target greater than zero.");
        if (metric is GoalMetric.ActivityCount or GoalMetric.ActiveDays && target != Math.Truncate(target))
            throw new ArgumentException("Activities and active days need a whole-number target.");
        SportKind? sport = null;
        if (Sport.Length > 0)
        {
            if (!Enum.TryParse<SportKind>(Sport, out var parsed) || !Enum.IsDefined(parsed)) throw new ArgumentException("Choose a valid sport.");
            sport = parsed;
        }
        return new(Name.Trim(), sport, target * GoalDisplay.Scale(metric));
    }
}

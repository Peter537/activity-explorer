using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Components.Shared;
using System.Globalization;

namespace ActivityExplorer.Web.Services;

internal static class ProgressDisplay
{
    public static IReadOnlyList<string> Metrics { get; } = ["activities", "distance", "moving", "ascent", "active-days"];
    public static string Label(string metric) => metric switch
    {
        "activities" => "Activities",
        "moving" => "Moving time",
        "ascent" => "Ascent",
        "active-days" => "Active days",
        _ => "Distance"
    };
    public static string Unit(string metric) => metric switch
    {
        "activities" => "activities",
        "moving" => "hours",
        "ascent" => "m",
        "active-days" => "days",
        _ => "km"
    };
    public static double Value(ReportingTotals totals, string metric) => metric switch
    {
        "activities" => totals.ActivityCount,
        "moving" => totals.MovingSeconds,
        "ascent" => totals.ElevationMeters,
        "active-days" => totals.ActiveDays,
        _ => totals.DistanceMeters
    };
    public static double ChartValue(ReportingTotals totals, string metric) => Value(totals, metric) / (metric == "distance" ? 1000 : metric == "moving" ? 3600 : 1);
    public static string FormatValue(string metric, double value) => metric switch
    {
        "distance" => Format.Distance(value),
        "moving" => Format.Duration(value),
        "ascent" => $"{value:N0} m",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };
    public static string Comparison(string metric, double selected, double baseline)
    {
        var delta = selected - baseline;
        var absolute = (delta > 0 ? "+" : delta < 0 ? "−" : "") + FormatValue(metric, Math.Abs(delta));
        var percent = ReportingProgress.PercentageChange(selected, baseline) is { } percentage
            ? $"{percentage:+0.0;-0.0;0.0}%" : "percentage unavailable (zero baseline)";
        return $"Compared with {FormatValue(metric, baseline)} · {absolute} · {percent}";
    }
}

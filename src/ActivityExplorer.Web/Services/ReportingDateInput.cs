using System.Globalization;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Services;

public sealed class ReportingDateInput
{
    public string Period { get; set; } = "all-time";
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public static ReportingDateInput FromQuery(string? period, string? from, string? to) => new()
    {
        Period = string.IsNullOrWhiteSpace(period)
            ? string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to) ? "all-time" : "custom"
            : period,
        From = from ?? "",
        To = to ?? ""
    };

    public ReportingDateSelection Selection => ReportingDates.Parse(Period, From, To);

    public void SelectPeriod(string period, IReadOnlyList<ResolvedOwnerPeriod> resolved)
    {
        Period = period;
        var ranges = resolved.Select(x => (x.From, x.To)).Distinct().ToArray();
        From = period == "custom" && ranges.Length == 1 ? DateText(ranges[0].From) : "";
        To = period == "custom" && ranges.Length == 1 ? DateText(ranges[0].To) : "";
    }

    public IEnumerable<KeyValuePair<string, string>> QueryValues()
    {
        var selection = Selection;
        if (selection.Preset == ReportingPreset.Custom)
        {
            if (selection.From.HasValue) yield return new("from", DateText(selection.From));
            if (selection.To.HasValue) yield return new("to", DateText(selection.To));
        }
        else if (selection.Preset != ReportingPreset.AllTime)
            yield return new("period", ReportingDates.ToSlug(selection.Preset));
    }

    public static string DateText(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
}

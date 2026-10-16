using System.Globalization;
using ActivityExplorer.Core.Models;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class SegmentDetailPage
{
    [Parameter, SupplyParameterFromQuery(Name = "effort-from")] public string? EffortFrom { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "effort-to")] public string? EffortTo { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "effort-min")] public string? EffortMinimum { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "effort-max")] public string? EffortMaximum { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "effort-sort")] public string? EffortSort { get; set; }

    private string _effortFrom = "";
    private string _effortTo = "";
    private string _effortMinimum = "";
    private string _effortMaximum = "";
    private string _effortSort = "rank";
    private string? _historyError;
    private SegmentEffortFilter? _historyFilter = new();
    private IReadOnlyList<SegmentEffortSummary> _historyEfforts = [];

    private void ReadEffortFilters()
    {
        _effortFrom = EffortFrom ?? "";
        _effortTo = EffortTo ?? "";
        _effortMinimum = EffortMinimum ?? "";
        _effortMaximum = EffortMaximum ?? "";
        _effortSort = string.IsNullOrWhiteSpace(EffortSort) ? "rank" : EffortSort;
        _historyFilter = ParseEffortFilters();
        RefreshEffortHistory();
    }

    private SegmentEffortFilter? ParseEffortFilters()
    {
        _historyError = null;
        try
        {
            var filter = new SegmentEffortFilter(ParseDate(_effortFrom, "Efforts from"), ParseDate(_effortTo, "Efforts to"),
                ParseSeconds(_effortMinimum), ParseSeconds(_effortMaximum), _effortSort);
            SegmentEffortFinding.Validate(filter);
            return filter;
        }
        catch (ArgumentException exception)
        {
            _historyError = exception.Message;
            return null;
        }
    }

    private void RefreshEffortHistory()
    {
        _historyEfforts = [];
        if (_segment is null || _historyFilter is null) return;
        try { _historyEfforts = SegmentEffortFinding.Apply(_segment.Efforts, _historyFilter, _segment.OwnerTimeZoneId); }
        catch (InvalidOperationException exception) { _historyError = exception.Message; }
    }

    private void ApplyEffortFilters()
    {
        var filter = ParseEffortFilters();
        if (filter is null) return;
        _historyFilter = filter;
        RefreshEffortHistory();
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["effort-from"] = filter.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["effort-to"] = filter.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["effort-min"] = filter.MinimumElapsedSeconds,
            ["effort-max"] = filter.MaximumElapsedSeconds,
            ["effort-sort"] = filter.Sort == "rank" ? null : filter.Sort
        }));
    }

    private void ResetEffortFilters() => Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
    {
        ["effort-from"] = null,
        ["effort-to"] = null,
        ["effort-min"] = null,
        ["effort-max"] = null,
        ["effort-sort"] = null
    }));

    private bool IsOutsideHistory(Guid? id) => id.HasValue && _segment!.Efforts.Any(effort => effort.Id == id) &&
        !_historyEfforts.Any(effort => effort.Id == id);

    private void UseHistoryBaseline(Guid id) => ChangePair(id.ToString(),
        Guid.TryParse(ComparisonId, out var comparison) && comparison != id ? comparison.ToString() : null);

    private void UseHistoryComparison(Guid id) => ChangePair(
        Guid.TryParse(BaselineId, out var baseline) && baseline != id ? baseline.ToString() : null, id.ToString());

    private int EffortPass(SegmentEffortSummary effort) => _segment!.Efforts.Where(value => value.ActivityId == effort.ActivityId)
        .OrderBy(value => value.StartPointIndex).ThenBy(value => value.Id).TakeWhile(value => value.Id != effort.Id).Count() + 1;

    private DateTimeOffset EffortLocalTime(SegmentEffortSummary effort)
    {
        try { return TimeZoneInfo.ConvertTime(effort.StartTime, ReportingTimeZone.Resolve(_segment!.OwnerTimeZoneId)); }
        catch (InvalidOperationException) { return effort.StartTime; }
    }

    private static DateOnly? ParseDate(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new ArgumentException($"{label} must be a valid date.");
    }

    private static double? ParseSeconds(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)) return seconds;
        throw new ArgumentException("Elapsed bounds must be finite, non-negative seconds.");
    }
}

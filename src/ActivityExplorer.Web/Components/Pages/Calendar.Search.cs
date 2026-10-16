using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Calendar
{
    private ActivityCriteria? _appliedCriteria;
    private IReadOnlyList<TagSummary> _searchTags = [];
    private ActivityCriteria CalendarLinkCriteria => _appliedCriteria ?? new(Sport: _appliedSport);

    private static Dictionary<string, string?> CalendarFields(string month, string view) => new()
    {
        ["month"] = string.IsNullOrWhiteSpace(month) ? null : month,
        ["view"] = view is "calendar" or "list" ? view : null
    };

    private string CalendarDashboardUrl()
    {
        var month = _calendar?.Month ?? _displayMonth;
        var fields = new Dictionary<string, string?>();
        if (month.HasValue)
        {
            fields["period"] = "custom";
            fields["from"] = IsoDate(month.Value);
            fields["to"] = IsoDate(new(month.Value.Year, month.Value.Month, DateTime.DaysInMonth(month.Value.Year, month.Value.Month)));
        }
        return ActivitySearchQuery.Build("/", CalendarLinkCriteria, ProfileState.SelectedOwnerId, fields);
    }

    private Task ClearCalendarSearch() => NavigateCalendarSearch(ActivitySearchQuery.Build("/calendar",
        new(Sport: Sport), ProfileState.SelectedOwnerId, CalendarFields(_monthInput, _view)));

    private Task OpenCalendarSearch(SavedSearchCriteria criteria)
    {
        var owner = ProfileState.Profiles.FirstOrDefault(profile => profile.Id == ProfileState.SelectedOwnerId)
            ?? throw new InvalidOperationException("Select a profile before opening a saved search.");
        var period = ReportingDates.Resolve(criteria.Dates, owner.Id, owner.DisplayName, owner.TimeZoneId, Clock.GetUtcNow());
        var month = period.From?.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)
            ?? TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), ReportingTimeZone.Resolve(owner.TimeZoneId))
                .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        return NavigateCalendarSearch(ActivitySearchQuery.Build("/calendar", criteria.ToActivityCriteria(),
            owner.Id, CalendarFields(month, _appliedView)));
    }

    private async Task NavigateCalendarSearch(string target)
    {
        if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) await LoadAsync();
        else Navigation.NavigateTo(target);
    }
}

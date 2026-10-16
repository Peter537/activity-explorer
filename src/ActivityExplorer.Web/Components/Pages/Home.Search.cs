using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Home
{
    private ActivityCriteria? _appliedCriteria;
    private IReadOnlyList<TagSummary> _searchTags = [];
    private ActivityCriteria LinkCriteria => _appliedCriteria ?? new(Sport: RecordDisplay.Parse<SportKind>(QuerySport));

    private Dictionary<string, string?> DashboardFields(ReportingDateInput dates)
    {
        var fields = dates.QueryValues().ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        fields["period"] = dates.Period;
        fields["compare"] = _comparison;
        fields["group"] = _grouping;
        fields["metric"] = _metric;
        return fields;
    }

    private Dictionary<string, string?> AppliedActivityDates()
    {
        var dates = AppliedDates;
        var fields = dates.QueryValues().ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        fields["period"] = dates.Period;
        return fields;
    }

    private string DashboardActivityUrl(ActivityCriteria criteria) =>
        ActivitySearchQuery.Build("/activities", criteria, ProfileState.SelectedOwnerId, AppliedActivityDates());

    private string SportActivitiesUrl(SportKind sport) => DashboardActivityUrl(LinkCriteria with { Sport = sport });

    private string DeviceActivitiesUrl(NamedTotal device) =>
        DashboardActivityUrl(LinkCriteria with { Device = device.Name, DeviceExact = true });

    private string GearActivitiesUrl(NamedTotal gear) => DashboardActivityUrl(LinkCriteria with
    {
        Gear = gear.IsUnassigned ? null : gear.Name,
        GearUnassigned = gear.IsUnassigned
    });

    private Task ClearDashboardSearch() => NavigateDashboardSearch(ActivitySearchQuery.Build("/",
        new(Sport: RecordDisplay.Parse<SportKind>(_sport)), ProfileState.SelectedOwnerId, DashboardFields(AppliedDates)));

    private Task OpenDashboardSearch(SavedSearchCriteria criteria)
    {
        var dates = new ReportingDateInput
        {
            Period = ReportingDates.ToSlug(criteria.Dates.Preset),
            From = ReportingDateInput.DateText(criteria.Dates.From),
            To = ReportingDateInput.DateText(criteria.Dates.To)
        };
        return NavigateDashboardSearch(ActivitySearchQuery.Build("/", criteria.ToActivityCriteria(),
            ProfileState.SelectedOwnerId, DashboardFields(dates)));
    }

    private async Task NavigateDashboardSearch(string target)
    {
        if (new Uri(Navigation.Uri).PathAndQuery == target) await LoadAsync();
        else Navigation.NavigateTo(target);
    }
}

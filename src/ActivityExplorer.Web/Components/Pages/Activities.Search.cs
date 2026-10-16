using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Activities
{
    [SupplyParameterFromQuery(Name = "textMode")] public string? QueryTextMode { get; set; }
    [SupplyParameterFromQuery(Name = "gear")] public string? QueryGear { get; set; }
    [SupplyParameterFromQuery(Name = "gearUnassigned")] public string? QueryGearUnassigned { get; set; }
    [SupplyParameterFromQuery(Name = "deviceExact")] public string? QueryDeviceExact { get; set; }
    [SupplyParameterFromQuery(Name = "indoor")] public string? QueryIndoor { get; set; }
    [SupplyParameterFromQuery(Name = "gps")] public string? QueryGps { get; set; }
    [SupplyParameterFromQuery(Name = "minDistance")] public string? QueryMinDistance { get; set; }
    [SupplyParameterFromQuery(Name = "maxDistance")] public string? QueryMaxDistance { get; set; }
    [SupplyParameterFromQuery(Name = "minMoving")] public string? QueryMinMoving { get; set; }
    [SupplyParameterFromQuery(Name = "maxMoving")] public string? QueryMaxMoving { get; set; }
    [SupplyParameterFromQuery(Name = "minAscent")] public string? QueryMinAscent { get; set; }
    [SupplyParameterFromQuery(Name = "maxAscent")] public string? QueryMaxAscent { get; set; }
    [SupplyParameterFromQuery(Name = "tagMode")] public string? QueryTagMode { get; set; }
    [SupplyParameterFromQuery(Name = "tagPresence")] public string? QueryTagPresence { get; set; }
    [SupplyParameterFromQuery(Name = "excludeTags")] public string? QueryExcludedTags { get; set; }

    private ActivitySearchInput _searchInput = new();
    private ActivityFilterOptions _filterOptions = new([], []);
    private IReadOnlyList<Guid> _excludedTagIds = [];
    private string? _criteriaError;
    private string? _criteriaQueryError;

    private void ReadSearchInput()
    {
        _criteriaQueryError = new[] { QueryGearUnassigned, QueryDeviceExact }
            .Any(value => value is not null and not "yes" and not "no")
            ? "The equipment filter link is invalid. Reset the filters or reopen a saved search." : null;
        _searchInput = new()
        {
            TextMode = QueryTextMode ?? (string.IsNullOrWhiteSpace(QuerySearch) ? "words" : "literal"),
            Gear = QueryGear ?? "",
            GearUnassigned = QueryGearUnassigned == "yes",
            DeviceExact = QueryDeviceExact == "yes",
            Indoor = QueryIndoor ?? "",
            Gps = QueryGps ?? "",
            TagMode = QueryTagMode ?? "any",
            TagPresence = QueryTagPresence ?? "any",
            MinDistance = ActivitySearchInput.DisplayRaw(QueryMinDistance, 1000),
            MaxDistance = ActivitySearchInput.DisplayRaw(QueryMaxDistance, 1000),
            MinMoving = ActivitySearchInput.DisplayRaw(QueryMinMoving, 60),
            MaxMoving = ActivitySearchInput.DisplayRaw(QueryMaxMoving, 60),
            MinAscent = QueryMinAscent ?? "",
            MaxAscent = QueryMaxAscent ?? ""
        };
    }

    private ActivityCriteria CurrentCriteria() => _searchInput.Build(Sport, _search, HasPower, _device, _tagIds, _excludedTagIds);

    private string SearchViewUrl(string path)
    {
        var filter = _result!.EffectiveFilter;
        var fields = new Dictionary<string, string?>();
        if (path == "/calendar")
        {
            var first = filter.ResolvedPeriods?.Select(period => period.From).Where(date => date.HasValue).Min();
            if (first.HasValue) fields["month"] = first.Value.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            var dates = new ReportingDateInput
            {
                Period = ReportingDates.ToSlug(filter.Period ?? ReportingPreset.Custom),
                From = ReportingDateInput.DateText(filter.From),
                To = ReportingDateInput.DateText(filter.To)
            };
            foreach (var pair in dates.QueryValues()) fields[pair.Key] = pair.Value;
        }
        return ActivitySearchQuery.Build(path, filter.GetCriteria(), ProfileState.SelectedOwnerId, fields);
    }

    private bool ValidateCriteria()
    {
        if (_criteriaQueryError is not null) { _criteriaError = _criteriaQueryError; return false; }
        try { _ = CurrentCriteria(); _criteriaError = null; return true; }
        catch (ArgumentException exception) { _criteriaError = exception.Message; return false; }
    }

    private int SearchExtraCount => new[]
    {
        _searchInput.Gear.Length > 0, _searchInput.GearUnassigned, _searchInput.DeviceExact,
        _searchInput.Indoor.Length > 0, _searchInput.Gps.Length > 0,
        _searchInput.MinDistance.Length > 0, _searchInput.MaxDistance.Length > 0,
        _searchInput.MinMoving.Length > 0, _searchInput.MaxMoving.Length > 0,
        _searchInput.MinAscent.Length > 0, _searchInput.MaxAscent.Length > 0,
        _searchInput.TagMode != "any", _searchInput.TagPresence != "any", _excludedTagIds.Count > 0
    }.Count(value => value);

    private void ExcludedTagsChanged(IReadOnlyList<Guid> ids)
    {
        _excludedTagIds = ids;
        _tagOwner = _tagIds.Count + ids.Count > 0 ? ProfileState.SelectedOwnerId : null;
        _tagQueryError = null;
        ValidateTagFilter();
        FiltersChanged();
    }
}

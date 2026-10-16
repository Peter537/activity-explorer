using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Activities
{
    private IReadOnlyList<TagSummary> _tags = [];
    private IReadOnlyList<Guid> _tagIds = [], _batchIds = [];
    private Guid? _tagOwner;
    private string? _tagError, _tagQueryError;
    private bool _switchingBookmark, _batchEditing, _batchAll;
    private int _organizationRevision, _batchGeneration;

    private SavedSearchCriteria? AppliedSearchCriteria
    {
        get
        {
            if (_result is null || ActionsBlocked || HasImportScope) return null;
            var filter = _result.EffectiveFilter;
            var criteria = filter.GetCriteria();
            SavedTagReference Reference(Guid id) => new(id, _tags.FirstOrDefault(tag => tag.Id == id)?.Name ?? "Missing tag");
            return new(2, criteria.Sport, criteria.Search, criteria.HasPower, criteria.Device,
                (criteria.TagIds ?? []).Select(Reference).ToArray(), filter.Sort,
                new(filter.Period ?? ReportingPreset.Custom, filter.From, filter.To),
                criteria.TextMode, criteria.DeviceExact, criteria.Gear, criteria.GearUnassigned, criteria.IsIndoor, criteria.HasGps,
                criteria.MinDistanceMeters, criteria.MaxDistanceMeters, criteria.MinMovingSeconds, criteria.MaxMovingSeconds,
                criteria.MinAscentMeters, criteria.MaxAscentMeters, criteria.TagMode,
                (criteria.ExcludedTagIds ?? []).Select(Reference).ToArray(), criteria.TagPresence);
        }
    }

    private string SelectionKey() => System.Text.Json.JsonSerializer.Serialize(new
    {
        ProfileState.SelectedOwnerId,
        ImportBatch = _importScope?.BatchId,
        ImportOwner = _importScope?.OwnerId,
        _search,
        _sport,
        _power,
        _device,
        _sort,
        _dates.Period,
        _dates.From,
        _dates.To,
        _tagOwner,
        Tags = _tagIds.Order().ToArray(),
        ExcludedTags = _excludedTagIds.Order().ToArray(),
        Extra = _searchInput
    });

    private void ReadTagQuery()
    {
        _tagQueryError = _tagError = null;
        _tagIds = [];
        _excludedTagIds = [];
        _tagOwner = null;
        if (string.IsNullOrWhiteSpace(QueryTags) && string.IsNullOrWhiteSpace(QueryExcludedTags)) return;
        var values = (QueryTags ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var excluded = (QueryExcludedTags ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Concat(excluded).Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty) ||
            !Guid.TryParse(QueryTagOwner, out var owner) || owner == Guid.Empty)
        {
            _tagQueryError = _tagError = "The tag filter link is invalid. Remove the tag filter or reopen a saved search.";
            return;
        }
        _tagOwner = owner;
        _tagIds = values.Select(Guid.Parse).Distinct().Order().ToArray();
        _excludedTagIds = excluded.Select(Guid.Parse).Distinct().Order().ToArray();
    }

    private bool ValidateTagFilter()
    {
        _tagError = _tagQueryError;
        if (_tagError is not null) return false;
        if (_tagIds.Count + _excludedTagIds.Count == 0) return true;
        if (_tagOwner != ProfileState.SelectedOwnerId)
            _tagError = "This tag filter belongs to another profile. Switch to that profile or explicitly remove the tag filter.";
        else if (_tagIds.Concat(_excludedTagIds).Any(id => _tags.All(tag => tag.Id != id)))
            _tagError = "One or more filtered tags have been deleted. Choose valid tags or explicitly remove the tag filter.";
        return _tagError is null;
    }

    private async Task LoadOrganization(int revision, CancellationToken cancellationToken)
    {
        var tags = ProfileState.SelectedOwnerId is { } owner
            ? await Organization.ListTagsAsync(owner, cancellationToken) : [];
        var options = await ActivitiesService.GetFilterOptionsAsync(ProfileState.SelectedOwnerId, cancellationToken);
        if (revision == _revision) { _tags = tags; _filterOptions = options; }
    }

    private void TagFiltersChanged(IReadOnlyList<Guid> ids)
    {
        _tagIds = ids;
        _tagOwner = ids.Count + _excludedTagIds.Count > 0 ? ProfileState.SelectedOwnerId : null;
        _tagQueryError = null;
        ValidateTagFilter();
        FiltersChanged();
    }

    private Task ClearTagFilters()
    {
        _excludedTagIds = [];
        _searchInput.TagMode = "any";
        _searchInput.TagPresence = "any";
        TagFiltersChanged([]);
        return Apply();
    }

    private void SwitchBookmarkProfile()
    {
        if (_tagOwner is not { } owner || !ProfileState.Profiles.Any(profile => profile.Id == owner)) return;
        _switchingBookmark = true;
        ProfileState.Select(owner);
    }

    private async Task OrganizationChanged(Guid ownerId)
    {
        if (ProfileState.SelectedOwnerId != ownerId) return;
        _organizationRevision++;
        ClearSelectionAndConfirmation();
        if (_dirty)
        {
            await LoadOrganization(_revision, CancellationToken.None);
            ValidateTagFilter();
        }
        else await LoadAsync(true);
    }

    private Task OpenSavedSearch(Guid ownerId, SavedSearchCriteria criteria)
    {
        if (ProfileState.SelectedOwnerId != ownerId) return Task.CompletedTask;
        ClearImportScope();
        ClearSelectionAndConfirmation();
        _search = criteria.Search ?? "";
        _sport = criteria.Sport?.ToString() ?? "";
        _power = criteria.HasPower switch { true => "yes", false => "no", _ => "" };
        _device = criteria.Device ?? "";
        _criteriaQueryError = null;
        _searchInput = ActivitySearchInput.FromCriteria(criteria.ToActivityCriteria());
        _sort = criteria.Sort;
        _dates = new() { Period = ReportingDates.ToSlug(criteria.Dates.Preset), From = ReportingDateInput.DateText(criteria.Dates.From), To = ReportingDateInput.DateText(criteria.Dates.To) };
        _tagIds = criteria.Tags.Select(tag => tag.Id).ToArray();
        _excludedTagIds = (criteria.ExcludedTags ?? []).Select(tag => tag.Id).ToArray();
        _tagOwner = _tagIds.Count + _excludedTagIds.Count > 0 ? ProfileState.SelectedOwnerId : null;
        _tagError = _tagQueryError = null;
        _page = 1;
        return NavigateOrLoadAsync();
    }

    private void BeginBatch(bool allMatching)
    {
        if (ActionsBlocked || _deleteConfirming || !ProfileState.SelectedOwnerId.HasValue || (!allMatching && _selectedIds.Count == 0)) return;
        _batchAll = allMatching;
        _batchIds = _selectedIds.ToArray();
        _batchGeneration++;
        _batchEditing = true;
    }

    private void CancelBatch() { _batchGeneration++; _batchEditing = false; _batchIds = []; }

    private async Task BatchApplied(Guid ownerId, int generation, BatchEditResult result)
    {
        if (ProfileState.SelectedOwnerId != ownerId || !_batchEditing || generation != _batchGeneration) return;
        ClearSelectionAndConfirmation();
        _statusMessage = $"Reviewed {result.ReviewedCount:N0} activities; changed {result.ChangedCount:N0}.";
        _organizationRevision++;
        await LoadAsync(true);
    }

    private async Task RefreshOrganizationActivities()
    {
        ClearSelectionAndConfirmation();
        await LoadAsync();
    }
}

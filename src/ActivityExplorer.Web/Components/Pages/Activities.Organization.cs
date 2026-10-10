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

    private SavedSearchCriteria? AppliedSearchCriteria => _result is null || ActionsBlocked ? null : new(
        1, _result.EffectiveFilter.Sport, _result.EffectiveFilter.Search, _result.EffectiveFilter.HasPower,
        _result.EffectiveFilter.Device, (_result.EffectiveFilter.TagIds ?? []).Select(id => new SavedTagReference(id, _tags.FirstOrDefault(tag => tag.Id == id)?.Name ?? "Missing tag")).ToArray(),
        _result.EffectiveFilter.Sort, new(_result.EffectiveFilter.Period ?? ReportingPreset.Custom,
            _result.EffectiveFilter.From, _result.EffectiveFilter.To));

    private string SelectionKey() => System.Text.Json.JsonSerializer.Serialize(new
    {
        ProfileState.SelectedOwnerId,
        _search,
        _sport,
        _power,
        _device,
        _sort,
        _dates.Period,
        _dates.From,
        _dates.To,
        _tagOwner,
        Tags = _tagIds.Order().ToArray()
    });

    private void ReadTagQuery()
    {
        _tagQueryError = _tagError = null;
        _tagIds = [];
        _tagOwner = null;
        if (string.IsNullOrWhiteSpace(QueryTags)) return;
        var values = QueryTags.Split(',', StringSplitOptions.TrimEntries);
        if (values.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty) ||
            !Guid.TryParse(QueryTagOwner, out var owner) || owner == Guid.Empty)
        {
            _tagQueryError = _tagError = "The tag filter link is invalid. Remove the tag filter or reopen a saved search.";
            return;
        }
        _tagOwner = owner;
        _tagIds = values.Select(Guid.Parse).Distinct().Order().ToArray();
    }

    private bool ValidateTagFilter()
    {
        _tagError = _tagQueryError;
        if (_tagError is not null) return false;
        if (_tagIds.Count == 0) return true;
        if (_tagOwner != ProfileState.SelectedOwnerId)
            _tagError = "This tag filter belongs to another profile. Switch to that profile or explicitly remove the tag filter.";
        else if (_tagIds.Any(id => _tags.All(tag => tag.Id != id)))
            _tagError = "One or more filtered tags have been deleted. Choose valid tags or explicitly remove the tag filter.";
        return _tagError is null;
    }

    private async Task LoadOrganization(int revision, CancellationToken cancellationToken)
    {
        var tags = ProfileState.SelectedOwnerId is { } owner
            ? await Organization.ListTagsAsync(owner, cancellationToken) : [];
        if (revision == _revision) _tags = tags;
    }

    private void TagFiltersChanged(IReadOnlyList<Guid> ids)
    {
        _tagIds = ids;
        _tagOwner = ids.Count > 0 ? ProfileState.SelectedOwnerId : null;
        _tagQueryError = null;
        ValidateTagFilter();
        FiltersChanged();
    }

    private Task ClearTagFilters()
    {
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
        ClearSelectionAndConfirmation();
        _search = criteria.Search ?? "";
        _sport = criteria.Sport?.ToString() ?? "";
        _power = criteria.HasPower switch { true => "yes", false => "no", _ => "" };
        _device = criteria.Device ?? "";
        _sort = criteria.Sort;
        _dates = new() { Period = ReportingDates.ToSlug(criteria.Dates.Preset), From = ReportingDateInput.DateText(criteria.Dates.From), To = ReportingDateInput.DateText(criteria.Dates.To) };
        _tagIds = criteria.Tags.Select(tag => tag.Id).ToArray();
        _tagOwner = _tagIds.Count > 0 ? ProfileState.SelectedOwnerId : null;
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

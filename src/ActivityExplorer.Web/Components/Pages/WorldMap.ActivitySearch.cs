using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;

namespace ActivityExplorer.Web.Components.Pages;

public partial class WorldMap
{
    private ActivityCriteria _activityCriteria = new();
    private Guid? _activityTagOwner;
    private IReadOnlyList<TagSummary> _lineTags = [];
    private bool _lineSearchLoading;

    private void ReadLineSearch()
    {
        _lineSearchLoading = false;
        _lineTags = [];
        var sport = Enum.TryParse<SportKind>(_sport, true, out var parsed) ? parsed : (SportKind?)null;
        _activityCriteria = new(Sport: sport);
        _activityTagOwner = null;
        if (_view != "lines") return;
        var selection = ActivitySearchQuery.Read(Navigation.Uri);
        _activityCriteria = selection.Criteria with { Sport = sport };
        _activityTagOwner = selection.TagOwner;
        _activityCriteria = ReadSharedCriteria(sport, ProfileState.SelectedOwnerId);
    }

    private async Task LoadLineSearchAsync()
    {
        var generation = _generation;
        var owner = _appliedOwner;
        if (!owner.HasValue) return;
        var request = _request = new CancellationTokenSource();
        var criteria = _activityCriteria;
        _lineSearchLoading = true;
        try
        {
            var tags = await Organization.ListTagsAsync(owner.Value, request.Token);
            if (_disposed || generation != _generation) return;
            _lineTags = tags;
            if ((criteria.TagIds ?? []).Concat(criteria.ExcludedTagIds ?? []).Any(id => tags.All(tag => tag.Id != id)))
                throw new InvalidOperationException("One or more activity search tags have been deleted. Repair the saved search or clear the activity search.");
            _hasMap = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_disposed || generation != _generation) return;
            _error = $"Activity search could not be opened. {exception.Message}";
            _hasMap = false;
        }
        finally { if (generation == _generation) _lineSearchLoading = false; }
    }

    private void ClearLineSearch() => Navigation.NavigateTo(ActivitySearchQuery.WithCriteria(Navigation.Uri,
        new(Sport: Enum.TryParse<SportKind>(_sport, true, out var sport) ? sport : null), ProfileState.SelectedOwnerId));

    private void OpenLineSearch(Guid owner, SavedSearchCriteria saved)
    {
        if (owner != ProfileState.SelectedOwnerId) return;
        var dates = new ReportingDateInput
        {
            Period = ReportingDates.ToSlug(saved.Dates.Preset),
            From = ReportingDateInput.DateText(saved.Dates.From),
            To = ReportingDateInput.DateText(saved.Dates.To)
        };
        var values = dates.QueryValues().ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        if (LayerQuery() is { } layers) values["layers"] = layers;
        Navigation.NavigateTo(ActivitySearchQuery.Build("/map", saved.ToActivityCriteria(), owner, values));
    }
}

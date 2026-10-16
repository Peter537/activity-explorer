using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Goals
{
    private bool HasFindingFilters => !string.IsNullOrWhiteSpace(_search) || _sport.Length > 0 || _metric.Length > 0 || _progress != "any";

    private GoalListFilter CurrentFilter()
    {
        SportKind? sport = null;
        GoalMetric? metric = null;
        if (_sport.Length > 0)
            sport = Enum.TryParse<SportKind>(_sport, true, out var parsedSport) && Enum.IsDefined(parsedSport)
                ? parsedSport : throw new ArgumentException("Choose a valid goal sport.");
        if (_metric.Length > 0)
            metric = Enum.TryParse<GoalMetric>(_metric, true, out var parsedMetric) && Enum.IsDefined(parsedMetric)
                ? parsedMetric : throw new ArgumentException("Choose a valid goal metric.");
        var progress = _progress switch
        {
            "any" => GoalProgressFilter.Any,
            "reached" => GoalProgressFilter.TargetReached,
            "unreached" => GoalProgressFilter.TargetNotReached,
            _ => throw new ArgumentException("Choose a valid goal progress filter.")
        };
        var view = _view switch { "all" => GoalListView.All, "archived" => GoalListView.Archived, _ => GoalListView.CurrentAndUpcoming };
        return new(Profiles.SelectedOwnerId, view, _search, sport, metric, progress);
    }

    private string BuildUri(string? view = null)
    {
        var values = new List<string>();
        void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) values.Add($"{name}={Uri.EscapeDataString(value)}"); }
        Add("view", (view ?? _view) == "current" ? null : view ?? _view);
        Add("q", _search.Trim());
        Add("sport", _sport.ToLowerInvariant());
        Add("metric", _metric.ToLowerInvariant());
        Add("progress", _progress == "any" ? null : _progress);
        return values.Count == 0 ? "/goals" : $"/goals?{string.Join('&', values)}";
    }

    private void FiltersEdited()
    {
        _load?.Cancel();
        _load = null;
        _revision++;
        _loading = false;
        _dirty = true;
        _error = null;
    }

    private async Task ApplyAsync()
    {
        try
        {
            _ = CurrentFilter();
            var target = BuildUri();
            if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) await LoadAsync();
            else Navigation.NavigateTo(target);
        }
        catch (ArgumentException exception) { _error = exception.Message; }
    }

    private Task ResetFiltersAsync()
    {
        _search = _sport = _metric = "";
        _progress = "any";
        return ApplyAsync();
    }
}

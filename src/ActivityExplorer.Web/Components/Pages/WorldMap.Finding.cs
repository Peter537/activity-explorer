using ActivityExplorer.Web.Components.Shared;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class WorldMap
{
    [Parameter, SupplyParameterFromQuery(Name = "layers")] public string? QueryLayers { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "newOnly")] public string? QueryNewOnly { get; set; }

    private bool _showActivities = true, _showRoutes = true, _showSegments = true, _newOnly;
    private MapCanvas? _worldMap;

    private void ReadFindingQuery()
    {
        var layers = QueryLayers is null ? new[] { "activities", "routes", "segments" } :
            QueryLayers == "none" ? [] : QueryLayers.Split(',', StringSplitOptions.TrimEntries);
        if (layers.Any(layer => layer is not ("activities" or "routes" or "segments")))
            throw new ArgumentException("Choose Activities, Routes, or Segments for the Lines layers.");
        _showActivities = layers.Contains("activities");
        _showRoutes = layers.Contains("routes");
        _showSegments = layers.Contains("segments");
        _newOnly = ExplorationQueryInput.NewOnly(QueryNewOnly);
    }

    private string? LayerQuery()
    {
        var layers = new List<string>();
        if (_showActivities) layers.Add("activities");
        if (_showRoutes) layers.Add("routes");
        if (_showSegments) layers.Add("segments");
        return layers.Count == 3 ? null : layers.Count == 0 ? "none" : string.Join(',', layers);
    }

    private void ChangeLayer(string layer, ChangeEventArgs args)
    {
        var enabled = args.Value is true;
        if (layer == "activities") _showActivities = enabled;
        if (layer == "routes") _showRoutes = enabled;
        if (layer == "segments") _showSegments = enabled;
        Navigation.NavigateTo(WithQuery(new() { ["layers"] = LayerQuery() }));
    }

    private void NewCellsChanged(ChangeEventArgs args) => Navigation.NavigateTo(WithQuery(new()
    {
        ["newOnly"] = args.Value?.ToString() == "new" ? "yes" : null,
        ["page"] = null,
        ["activityPage"] = null,
        ["cell"] = null
    }));

    private Task ShowCellOnMap() => _worldMap is not null && _selectedCell is { } cell
        ? _worldMap.FocusCellAsync(cell) : Task.CompletedTask;
}

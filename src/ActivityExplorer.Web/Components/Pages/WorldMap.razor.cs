using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class WorldMap
{
    [Parameter, SupplyParameterFromQuery(Name = "sport")] public string? QuerySport { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "from")] public string? QueryFrom { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "to")] public string? QueryTo { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "period")] public string? QueryPeriod { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "view")] public string? QueryView { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "coverage")] public string? QueryCoverage { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "through")] public string? QueryThrough { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "cell")] public string? QueryCell { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "page")] public string? QueryPage { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "activityPage")] public string? QueryActivityPage { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "month")] public string? QueryMonth { get; set; }
    private string _sport = "", _appliedSport = "", _view = "lines", _scopeKey = "";
    private ReportingDateInput _dates = new();
    private IReadOnlyList<ResolvedOwnerPeriod> _periods = [];
    private ReportingDateSelection _appliedDates = new(ReportingPreset.AllTime);
    private Guid? _appliedOwner;
    private DateTimeOffset _asOfUtc;
    private string? _dateError, _error;
    private bool _dirty, _hasMap, _cumulative, _building, _cancelled, _loadRequested, _disposed;
    private int _generation, _page = 1, _activityPage = 1;
    private int? _selectedCell;
    private DateOnly? _through, _historyMonth;
    private DateOnly? _timelineFirst, _timelineLast, _timelineThrough;
    private ExplorationResult? _result;
    private ExplorationCellDetail? _cellDetail;
    private ExplorationBuildProgress? _progress;
    private CancellationTokenSource? _request;

    protected override void OnInitialized() => ProfileState.Changed += ProfileChanged;

    protected override void OnParametersSet()
    {
        _request?.Cancel();
        _generation++;
        _error = null; _result = null; _cellDetail = null; _cancelled = false; _building = false; _loadRequested = false;
        _sport = Enum.TryParse<SportKind>(QuerySport, true, out var sport) && Enum.IsDefined(sport) ? sport.ToString() : "";
        _dates = ReportingDateInput.FromQuery(QueryPeriod, QueryFrom, QueryTo);
        var scopeKey = $"{ProfileState.SelectedOwnerId}|{_sport}|{QueryPeriod}|{QueryFrom}|{QueryTo}";
        if (_scopeKey != scopeKey || _asOfUtc == default)
        {
            _asOfUtc = Clock.GetUtcNow(); _scopeKey = scopeKey;
            _timelineFirst = _timelineLast = _timelineThrough = null;
        }
        _dirty = false;
        if (!ValidateDates()) { _hasMap = false; return; }
        try
        {
            if (!string.IsNullOrEmpty(QuerySport) && _sport.Length == 0)
                throw new ArgumentException("Choose a supported sport or All sports.");
            _view = ExplorationQueryInput.View(QueryView);
            _cumulative = ExplorationQueryInput.Cumulative(QueryCoverage);
            _through = ExplorationQueryInput.Date(QueryThrough, "Through");
            _timelineThrough = _through ?? _timelineThrough;
            _historyMonth = ExplorationQueryInput.Date(QueryMonth, "History month", true);
            _selectedCell = ExplorationQueryInput.Cell(QueryCell);
            _page = ExplorationQueryInput.Page(QueryPage);
            _activityPage = ExplorationQueryInput.Page(QueryActivityPage);
            _appliedSport = _sport;
            _appliedOwner = ProfileState.SelectedOwnerId;
            _appliedDates = _dates.Selection;
            _hasMap = true;
            var canonical = BuildUri();
            if (QueryPeriod is not null && Navigation.ToBaseRelativePath(Navigation.Uri) != canonical.TrimStart('/'))
            { Navigation.NavigateTo(canonical, replace: true); return; }
            _loadRequested = _view != "lines";
            _building = _loadRequested;
        }
        catch (ArgumentException exception) { _error = exception.Message; _hasMap = false; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_loadRequested || _disposed) return;
        _loadRequested = false;
        await LoadAsync();
    }

    private ExplorationQuery CurrentQuery => new(new(_appliedOwner,
        Enum.TryParse<SportKind>(_appliedSport, out var sport) ? sport : null), _appliedDates, _asOfUtc,
        _view == "exploration" && _cumulative, _through, _historyMonth, _page);

    private async Task LoadAsync(bool recheck = false)
    {
        _request?.Cancel(); _request?.Dispose();
        var request = _request = new();
        var token = request.Token;
        var generation = ++_generation;
        var query = CurrentQuery;
        _building = true; _cancelled = false; _error = null; _progress = null; _result = null; _cellDetail = null;
        StateHasChanged();
        try
        {
            var lastProgress = DateTimeOffset.MinValue;
            var progress = new Progress<ExplorationBuildProgress>(value =>
            {
                var now = Clock.GetUtcNow();
                if (value.Completed != value.Total && now - lastProgress < TimeSpan.FromMilliseconds(150)) return;
                lastProgress = now;
                _ = InvokeAsync(() => { if (!_disposed && generation == _generation && _building) { _progress = value; StateHasChanged(); } });
            });
            var status = await ExplorationIndex.GetStatusAsync(query.Scope, token);
            if (!status.IsComplete || recheck) await ExplorationIndex.BuildAsync(query.Scope, progress, recheck, token);
            var result = await ExplorationQueries.GetAsync(query, token);
            var detail = _selectedCell.HasValue && result.Index.IsComplete
                ? await ExplorationQueries.GetCellAsync(query, _selectedCell.Value, _activityPage, token) : null;
            if (_disposed || generation != _generation) return;
            _result = result; _cellDetail = detail; _periods = result.Periods;
            if (result.Summary is { } summary)
            {
                _timelineFirst = summary.FirstDate; _timelineLast = summary.LastDate;
                _timelineThrough = summary.Through ?? summary.LastDate;
            }
            if (!result.Index.IsComplete) _error = "The history changed during calculation. Retry to include the latest activities.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (generation == _generation && !_disposed) _cancelled = true; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        { if (generation == _generation && !_disposed) _error = exception.Message; }
        catch (Exception)
        { if (generation == _generation && !_disposed) _error = "Exploration could not be calculated. Retry to resume the saved work."; }
        finally
        { if (generation == _generation && !_disposed) { _building = false; StateHasChanged(); } }
    }

    private bool ValidateDates()
    {
        try
        {
            var selection = _dates.Selection;
            _dates.Period = ReportingDates.ToSlug(selection.Preset);
            _periods = ProfileState.Profiles.Where(x => !ProfileState.SelectedOwnerId.HasValue || x.Id == ProfileState.SelectedOwnerId)
                .Select(x => ReportingDates.Resolve(selection, x.Id, x.DisplayName, x.TimeZoneId, _asOfUtc)).ToArray();
            if (_periods.Count == 0) _ = ReportingDates.Resolve(selection, Guid.Empty, "", null, _asOfUtc);
            _dateError = null; return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { _dateError = exception.Message; _periods = []; return false; }
    }

    private string LineUrl(string kind) => MapUrl(kind, kind == "activities");
    private string? ExplorationUrl => _view != "lines" && _result?.Summary is not null && !_building && _error is null
        ? MapUrl("exploration", true) : null;
    private string MapUrl(string kind, bool dates)
    {
        var parts = new List<string>();
        if (_appliedOwner.HasValue) parts.Add($"ownerId={_appliedOwner}");
        if (_appliedSport.Length > 0) parts.Add($"sport={_appliedSport}");
        if (dates)
        {
            parts.Add($"period={ReportingDates.ToSlug(_appliedDates.Preset)}");
            if (_appliedDates.Preset == ReportingPreset.Custom)
            {
                if (_appliedDates.From.HasValue) parts.Add($"from={DateText(_appliedDates.From)}");
                if (_appliedDates.To.HasValue) parts.Add($"to={DateText(_appliedDates.To)}");
            }
            parts.Add($"asOf={Uri.EscapeDataString(_asOfUtc.ToString("O", CultureInfo.InvariantCulture))}");
        }
        if (kind == "exploration")
        {
            parts.Add($"view={_view}");
            if (_view == "exploration" && _cumulative)
            {
                parts.Add("coverage=cumulative");
                if (_result?.Summary?.Through is { } through) parts.Add($"through={DateText(through)}");
            }
        }
        return $"/internal/map/{kind}?{string.Join('&', parts)}";
    }

    private void RefreshMap()
    {
        _asOfUtc = Clock.GetUtcNow();
        if (!ValidateDates()) return;
        _selectedCell = null; _page = _activityPage = 1;
        var target = BuildUri();
        if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) OnParametersSet();
        else Navigation.NavigateTo(target);
    }
    private void FiltersChanged() { _dirty = true; ValidateDates(); }
    private void Reset() { _sport = ""; _dates = new(); _through = _historyMonth = null; RefreshMap(); }
    private string BuildUri()
    {
        var parts = new List<string>();
        if (_sport.Length > 0) parts.Add($"sport={_sport.ToLowerInvariant()}");
        foreach (var pair in _dates.QueryValues()) parts.Add($"{pair.Key}={Uri.EscapeDataString(pair.Value)}");
        if (_view != "lines") parts.Add($"view={_view}");
        if (_cumulative) parts.Add("coverage=cumulative");
        if (_through.HasValue) parts.Add($"through={DateText(_through)}");
        if (_selectedCell.HasValue) parts.Add($"cell={_selectedCell}");
        if (_page > 1) parts.Add($"page={_page}");
        if (_activityPage > 1) parts.Add($"activityPage={_activityPage}");
        if (_historyMonth.HasValue) parts.Add($"month={_historyMonth:yyyy-MM}");
        return "/map" + (parts.Count == 0 ? "" : "?" + string.Join('&', parts));
    }

    private string WithQuery(Dictionary<string, object?> values) => Navigation.GetUriWithQueryParameters(values);
    private string ViewUri(string view) => WithQuery(new() { ["view"] = view == "lines" ? null : view, ["cell"] = null, ["page"] = null, ["activityPage"] = null });
    private string CoverageUri(bool cumulative) => WithQuery(new() { ["coverage"] = cumulative ? "cumulative" : null, ["cell"] = null, ["page"] = null, ["activityPage"] = null });
    private string CellUri(int? cell) => WithQuery(new() { ["cell"] = cell, ["activityPage"] = null });
    private void SelectCell(int cell) => Navigation.NavigateTo(CellUri(cell));
    private void ThroughChanged(ChangeEventArgs args) => Navigation.NavigateTo(WithQuery(new() { ["through"] = args.Value?.ToString(), ["page"] = null, ["activityPage"] = null }));
    private void SliderChanged(ChangeEventArgs args)
    {
        if (int.TryParse(args.Value?.ToString(), CultureInfo.InvariantCulture, out var day) && day >= 0 && day <= DateOnly.MaxValue.DayNumber)
            Navigation.NavigateTo(WithQuery(new() { ["through"] = DateText(DateOnly.FromDayNumber(day)), ["page"] = null, ["activityPage"] = null }));
    }
    private void ChangeCellPage(int delta) => Navigation.NavigateTo(WithQuery(new() { ["page"] = (_result?.Cells.Page ?? 1) + delta }));
    private void ChangeContributionPage(int delta) => Navigation.NavigateTo(WithQuery(new() { ["activityPage"] = (_cellDetail?.Activities.Page ?? 1) + delta }));
    private void ChangeHistory(int months) => Navigation.NavigateTo(WithQuery(new() { ["month"] = _result!.HistoryMonth.AddMonths(months).ToString("yyyy-MM", CultureInfo.InvariantCulture) }));
    private static string DateText(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private void Cancel() => _request?.Cancel();
    private Task Retry() => _hasMap && _view != "lines" ? LoadAsync() : Task.CompletedTask;
    private Task Recheck() => LoadAsync(true);
    private Task ViewportSummaryChanged(ExplorationSummary summary) => !_building && _result?.Summary is { } current && current != summary
        ? LoadAsync() : Task.CompletedTask;
    private void ProfileChanged() => _ = InvokeAsync(() => { _selectedCell = null; _scopeKey = ""; RefreshMap(); StateHasChanged(); });
    public void Dispose() { _disposed = true; _generation++; _request?.Cancel(); _request?.Dispose(); ProfileState.Changed -= ProfileChanged; GC.SuppressFinalize(this); }
}

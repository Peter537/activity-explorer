using System.Globalization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace ActivityExplorer.Web.Components.Pages;

public partial class ImportsPage
{
    [SupplyParameterFromQuery(Name = "q")] public string? HistoryQuery { get; set; }
    [SupplyParameterFromQuery(Name = "status")] public string? HistoryStatus { get; set; }
    [SupplyParameterFromQuery(Name = "warnings")] public string? HistoryWarnings { get; set; }
    [SupplyParameterFromQuery(Name = "source")] public string? HistorySource { get; set; }
    [SupplyParameterFromQuery(Name = "kind")] public string? HistoryKind { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? HistoryFrom { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? HistoryTo { get; set; }
    [SupplyParameterFromQuery(Name = "limit")] public string? HistoryLimit { get; set; }
    private string _historySearch = "", _historyStatus = "", _historyWarnings = "", _historySource = "", _historyKind = "";
    private DateOnly? _historyFrom, _historyTo;
    private ImportHistoryFilter _appliedHistory = new();
    private string? _historyQueryError;
    private bool _historyDirty, _disposed;
    private int _historyRevision;
    private bool HistoryHasFilters => !string.IsNullOrWhiteSpace(HistoryQuery) || !string.IsNullOrWhiteSpace(HistoryStatus) ||
        !string.IsNullOrWhiteSpace(HistoryWarnings) || !string.IsNullOrWhiteSpace(HistorySource) || !string.IsNullOrWhiteSpace(HistoryKind) ||
        !string.IsNullOrWhiteSpace(HistoryFrom) || !string.IsNullOrWhiteSpace(HistoryTo) || HistoryLimit is not null;

    protected override async Task OnParametersSetAsync()
    {
        _historySearch = HistoryQuery ?? ""; _historyStatus = HistoryStatus ?? ""; _historyWarnings = HistoryWarnings ?? "";
        _historySource = HistorySource ?? ""; _historyKind = HistoryKind ?? "";
        _historyDirty = false; _historyQueryError = null; _historyLimit = 10;
        try
        {
            _historyFrom = Date(HistoryFrom); _historyTo = Date(HistoryTo);
            if (HistoryLimit is not null && (!int.TryParse(HistoryLimit, NumberStyles.None, CultureInfo.InvariantCulture, out _historyLimit) || _historyLimit < 10))
                throw new ArgumentException("Choose a history limit of at least 10.");
            _appliedHistory = CurrentHistoryFilter();
        }
        catch (ArgumentException exception) { _historyQueryError = exception.Message; }
        await LoadHistory();
    }

    private ImportHistoryFilter CurrentHistoryFilter()
    {
        var statuses = string.IsNullOrWhiteSpace(_historyStatus) ? [] : _historyStatus.Split(',', StringSplitOptions.TrimEntries)
            .Select(value => Parse<ImportStatus>(value) ?? throw new ArgumentException("Choose a valid import status.")).Distinct().ToArray();
        var warnings = _historyWarnings switch { "" => (bool?)null, "yes" => true, "no" => false, _ => throw new ArgumentException("Choose a valid warnings filter.") };
        if (_historyFrom > _historyTo) throw new ArgumentException("Imported from must be on or before Imported to.");
        return new(ProfileState.SelectedOwnerId, _historySearch.Trim(), statuses, warnings,
            Parse<SourceKind>(_historySource), Parse<ImportBatchKind>(_historyKind), _historyFrom, _historyTo);
    }

    private async Task ApplyHistoryFilters()
    {
        try { _appliedHistory = CurrentHistoryFilter(); _historyQueryError = null; _historyLimit = 10; await NavigateHistory(); }
        catch (ArgumentException exception) { _historyError = exception.Message; }
    }

    private Task ResetHistoryFilters()
    {
        _historySearch = _historyStatus = _historyWarnings = _historySource = _historyKind = "";
        _historyFrom = _historyTo = null;
        return ApplyHistoryFilters();
    }

    private async Task NavigateHistory()
    {
        var values = new Dictionary<string, string?>
        {
            ["q"] = Empty(_appliedHistory.Search),
            ["status"] = _appliedHistory.Statuses is { Count: > 0 } statuses ? string.Join(',', statuses) : null,
            ["warnings"] = _appliedHistory.HasWarnings is { } warnings ? warnings ? "yes" : "no" : null,
            ["source"] = _appliedHistory.Source?.ToString(),
            ["kind"] = _appliedHistory.Kind?.ToString(),
            ["from"] = Empty(ReportingDateInput.DateText(_appliedHistory.From)),
            ["to"] = Empty(ReportingDateInput.DateText(_appliedHistory.To)),
            ["limit"] = _historyLimit == 10 ? null : _historyLimit.ToString(CultureInfo.InvariantCulture)
        };
        var target = QueryHelpers.AddQueryString("/imports", values);
        if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) { _historyDirty = false; await LoadHistory(); }
        else Navigation.NavigateTo(target);
    }

    private void HistoryFiltersChanged() { _historyDirty = true; _historyError = null; }
    private string ImportTimeText(ImportBatchSummary import)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(import.CreatedAt, ReportingTimeZone.Resolve(
            ProfileState.Profiles.FirstOrDefault(profile => profile.Id == import.OwnerId)?.TimeZoneId)).ToString("g", CultureInfo.CurrentCulture);
        }
        catch (InvalidOperationException) { return $"{import.CreatedAt.UtcDateTime.ToString("g", CultureInfo.CurrentCulture)} UTC (profile timezone unavailable)"; }
    }
    private static string LinkedImportUrl(string path, ImportBatchSummary import) =>
        QueryHelpers.AddQueryString(path, new Dictionary<string, string?> { ["importBatch"] = import.Id.ToString(), ["importOwner"] = import.OwnerId.ToString() });
    private static string KindText(ImportBatchKind kind) => kind switch { ImportBatchKind.ActivityTransfer => "Activity transfer", ImportBatchKind.RouteImport => "Route import", _ => "File import" };
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateOnly? Date(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw new ArgumentException("Choose a valid imported date.");
    private static T? Parse<T>(string text) where T : struct, Enum => string.IsNullOrWhiteSpace(text) ? null :
        Enum.TryParse<T>(text, true, out var value) && Enum.IsDefined(value) ? value : throw new ArgumentException($"Choose a valid {typeof(T).Name} filter.");
}

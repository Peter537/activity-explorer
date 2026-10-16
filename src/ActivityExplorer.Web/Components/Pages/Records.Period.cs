using ActivityExplorer.Core.Models;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Records
{
    [SupplyParameterFromQuery(Name = "period")] public string? QueryPeriod { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? QueryFrom { get; set; }
    [SupplyParameterFromQuery(Name = "to")] public string? QueryTo { get; set; }
    private ReportingDateInput _dates = new();
    private ReportingDateSelection _appliedPeriod = new(ReportingPreset.AllTime);
    private IReadOnlyList<ResolvedOwnerPeriod> _periods = [];
    private string? _dateError;
    private bool _loading, _cancelled, _datesDirty, _disposed;
    private CancellationTokenSource? _cancellation;

    private bool ValidateRecordDates()
    {
        try
        {
            var selection = _dates.Selection;
            var instant = Clock.GetUtcNow();
            _periods = ProfileState.Profiles.Where(profile => !ProfileState.SelectedOwnerId.HasValue || profile.Id == ProfileState.SelectedOwnerId)
                .Select(profile => ReportingDates.Resolve(selection, profile.Id, profile.DisplayName, profile.TimeZoneId, instant)).ToArray();
            _dateError = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            _dateError = exception.Message;
            _periods = [];
            return false;
        }
    }

    private void RecordDatesChanged()
    {
        CancelCurrentCalculation();
        _loadVersion++;
        _loading = false;
        _datesDirty = true;
        _cancelled = false;
        ValidateRecordDates();
    }

    private async Task ApplyRecordPeriod()
    {
        if (!ValidateRecordDates()) return;
        var target = RecordDisplay.RecordsUrl(ParseSport(QuerySport), _scope, _dates.Selection);
        if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) await RefreshRecords();
        else Navigation.NavigateTo(target);
    }

    private void CancelRecordCalculation()
    {
        CancelCurrentCalculation();
        _loadVersion++;
        _loading = false;
        _cancelled = true;
        _records = null;
    }

    private void CancelCurrentCalculation()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        cancellation?.Cancel();
    }

    private string RecordReportingDate(PersonalRecord record)
    {
        var zone = _periods.FirstOrDefault(period => period.OwnerId == record.OwnerId)?.TimeZoneId
            ?? ProfileState.Profiles.FirstOrDefault(profile => profile.Id == record.OwnerId)?.TimeZoneId;
        return TimeZoneInfo.ConvertTime(record.ActivityDate, ReportingTimeZone.Resolve(zone))
            .ToString("d", System.Globalization.CultureInfo.CurrentCulture);
    }
}

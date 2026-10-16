using System.Globalization;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class GoalDetailPage
{
    private string _jumpDate = "";
    private string? _jumpError;
    private bool _jumping;

    private void EditionDateChanged(ChangeEventArgs args)
    {
        _jumpDate = args.Value?.ToString() ?? "";
        _jumpError = null;
    }

    private async Task FindEditionAsync()
    {
        if (_saving || _jumping || _editing || _confirmArchive || _detail is null) return;
        var revision = _revision;
        _jumping = true;
        _jumpError = null;
        try
        {
            if (!DateOnly.TryParseExact(_jumpDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new ArgumentException("Choose a valid edition date.");
            var location = await Goals.FindEditionAsync(Id, date, _lifetime.Token)
                ?? throw new InvalidOperationException("This goal is no longer available.");
            if (_disposed || revision != _revision) return;
            var target = GoalDisplay.Url(Id, location.Edition, location.HistoryPage, 1);
            if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) await ReloadAsync();
            else Navigation.NavigateTo(target);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_disposed || revision != _revision) return;
            _jumpError = exception is ArgumentException or InvalidOperationException
                ? exception.Message : "The edition could not be found. Try again.";
        }
        finally { _jumping = false; }
    }

    private async Task CurrentEdition()
    {
        var target = GoalDisplay.Url(Id);
        if (Navigation.ToBaseRelativePath(Navigation.Uri) == target.TrimStart('/')) await ReloadAsync();
        else Navigation.NavigateTo(target);
    }
}

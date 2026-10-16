using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Components.Pages;

public partial class Activities
{
    [SupplyParameterFromQuery(Name = "importBatch")] public string? QueryImportBatch { get; set; }
    [SupplyParameterFromQuery(Name = "importOwner")] public string? QueryImportOwner { get; set; }
    private ImportLinkScope? _importScope;
    private string? _importError;
    private bool HasImportScope => QueryImportBatch is not null || QueryImportOwner is not null;

    private bool ValidateImportScope()
    {
        _importScope = null;
        try
        {
            _importScope = ImportLinkScope.Parse(QueryImportBatch, QueryImportOwner);
            _importScope?.ValidateOwner(ProfileState.SelectedOwnerId);
            _importError = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { _importError = exception.Message; return false; }
    }

    private void RemoveImportScope() => Navigation.NavigateTo(ImportLinkScope.Remove(Navigation.Uri));
    private void ClearImportScope() { _importScope = null; _importError = null; QueryImportBatch = QueryImportOwner = null; }
}

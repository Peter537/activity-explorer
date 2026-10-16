using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using Microsoft.AspNetCore.Components;

namespace ActivityExplorer.Web.Services;

public abstract class ActivitySearchViewBase : ComponentBase
{
    [Inject] protected NavigationManager SearchNavigation { get; set; } = null!;
    [SupplyParameterFromQuery(Name = "q")] public string? SharedSearch { get; set; }
    [SupplyParameterFromQuery(Name = "power")] public string? SharedPower { get; set; }
    [SupplyParameterFromQuery(Name = "device")] public string? SharedDevice { get; set; }
    [SupplyParameterFromQuery(Name = "textMode")] public string? SharedTextMode { get; set; }
    [SupplyParameterFromQuery(Name = "deviceExact")] public string? SharedDeviceExact { get; set; }
    [SupplyParameterFromQuery(Name = "gear")] public string? SharedGear { get; set; }
    [SupplyParameterFromQuery(Name = "gearUnassigned")] public string? SharedGearUnassigned { get; set; }
    [SupplyParameterFromQuery(Name = "indoor")] public string? SharedIndoor { get; set; }
    [SupplyParameterFromQuery(Name = "gps")] public string? SharedGps { get; set; }
    [SupplyParameterFromQuery(Name = "minDistance")] public string? SharedMinDistance { get; set; }
    [SupplyParameterFromQuery(Name = "maxDistance")] public string? SharedMaxDistance { get; set; }
    [SupplyParameterFromQuery(Name = "minMoving")] public string? SharedMinMoving { get; set; }
    [SupplyParameterFromQuery(Name = "maxMoving")] public string? SharedMaxMoving { get; set; }
    [SupplyParameterFromQuery(Name = "minAscent")] public string? SharedMinAscent { get; set; }
    [SupplyParameterFromQuery(Name = "maxAscent")] public string? SharedMaxAscent { get; set; }
    [SupplyParameterFromQuery(Name = "tags")] public string? SharedTags { get; set; }
    [SupplyParameterFromQuery(Name = "excludeTags")] public string? SharedExcludedTags { get; set; }
    [SupplyParameterFromQuery(Name = "tagMode")] public string? SharedTagMode { get; set; }
    [SupplyParameterFromQuery(Name = "tagPresence")] public string? SharedTagPresence { get; set; }
    [SupplyParameterFromQuery(Name = "tagOwner")] public string? SharedTagOwner { get; set; }

    protected Guid? SearchTagOwner { get; private set; }

    protected ActivityCriteria ReadSharedCriteria(SportKind? sport, Guid? ownerId)
    {
        SearchTagOwner = null;
        var selection = ActivitySearchQuery.Read(SearchNavigation.Uri);
        SearchTagOwner = selection.TagOwner;
        if (selection.TagOwner.HasValue && selection.TagOwner != ownerId)
            throw new InvalidOperationException("This activity search's tags belong to another profile. Switch to that profile or clear the activity search.");
        return ActivityCriteriaValidation.Normalize(selection.Criteria with { Sport = sport });
    }
}

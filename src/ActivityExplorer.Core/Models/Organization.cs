using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record TagSummary(Guid Id, string Name, int ActivityCount = 0, int SavedSearchCount = 0);
public sealed record SavedTagReference(Guid Id, string Name);
public sealed record SavedSearchCriteria(
    int Version, SportKind? Sport, string? Search, bool? HasPower, string? Device,
    IReadOnlyList<SavedTagReference> Tags, string Sort, ReportingDateSelection Dates,
    ActivityTextMode TextMode = ActivityTextMode.Literal, bool DeviceExact = false,
    string? Gear = null, bool GearUnassigned = false, bool? IsIndoor = null, bool? HasGps = null,
    double? MinDistanceMeters = null, double? MaxDistanceMeters = null,
    double? MinMovingSeconds = null, double? MaxMovingSeconds = null,
    double? MinAscentMeters = null, double? MaxAscentMeters = null,
    ActivityTagMode TagMode = ActivityTagMode.Any, IReadOnlyList<SavedTagReference>? ExcludedTags = null,
    ActivityTagPresence TagPresence = ActivityTagPresence.Any)
{
    public ActivityCriteria ToActivityCriteria() => new(
        Sport, Search, HasPower, Device, Tags.Select(x => x.Id).ToArray(),
        Version == 1 ? ActivityTextMode.Literal : TextMode, DeviceExact, Gear, GearUnassigned, IsIndoor, HasGps,
        MinDistanceMeters, MaxDistanceMeters, MinMovingSeconds, MaxMovingSeconds, MinAscentMeters, MaxAscentMeters,
        TagMode, ExcludedTags?.Select(x => x.Id).ToArray(), TagPresence);
}
public sealed record SavedSearchSummary(
    Guid Id, string Name, SavedSearchCriteria? Criteria, string? Error, IReadOnlyList<SavedTagReference> MissingTags);
public enum GearEditMode { Unchanged, Set, Clear }
public sealed record BatchChangeRequest(
    IReadOnlyList<Guid> AddTagIds, IReadOnlyList<Guid> RemoveTagIds, GearEditMode GearMode, string? GearName);
public sealed record BatchMember(Guid Id, long MutationVersion);
public sealed record BatchReview(
    Guid OwnerId, IReadOnlyList<BatchMember> Members, BatchChangeRequest Changes,
    IReadOnlyList<TagSummary> AddTags, IReadOnlyList<TagSummary> RemoveTags);
public sealed record BatchEditResult(int ReviewedCount, int ChangedCount);

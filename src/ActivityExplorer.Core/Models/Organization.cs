using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record TagSummary(Guid Id, string Name, int ActivityCount = 0, int SavedSearchCount = 0);
public sealed record SavedTagReference(Guid Id, string Name);
public sealed record SavedSearchCriteria(
    int Version, SportKind? Sport, string? Search, bool? HasPower, string? Device,
    IReadOnlyList<SavedTagReference> Tags, string Sort, ReportingDateSelection Dates);
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

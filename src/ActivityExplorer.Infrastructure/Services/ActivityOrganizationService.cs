using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ActivityOrganizationService(
    IDbContextFactory<ExplorerDbContext> contextFactory,
    IOwnerMutationLock ownerMutationLock,
    IActivityQueryService activities) : IActivityOrganizationService
{
    private const int ChunkSize = 500;
    private static readonly JsonSerializerOptions CriteriaJsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<IReadOnlyList<TagSummary>> ListTagsAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        var tags = await db.Tags.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.Name)
            .Select(x => new TagSummary(x.Id, x.Name, db.ActivityTags.Count(a => a.TagId == x.Id), 0)).ToListAsync(cancellationToken);
        var searches = await db.SavedSearches.AsNoTracking().Where(x => x.OwnerId == ownerId).Select(x => x.CriteriaJson).ToListAsync(cancellationToken);
        var references = searches.Select(TryReadCriteria).Where(x => x is not null)
            .SelectMany(x => x!.Tags.Select(tag => tag.Id).Distinct()).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        await transaction.CommitAsync(cancellationToken);
        return tags.Select(tag => tag with { SavedSearchCount = references.GetValueOrDefault(tag.Id) }).ToArray();
    }

    public async Task<Guid> CreateTagAsync(Guid ownerId, string name, CancellationToken cancellationToken = default)
    {
        name = CleanName(name, 80, "Tag");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        var normalized = name.ToUpperInvariant();
        await RequireUniqueTagNameAsync(db, ownerId, normalized, null, cancellationToken);
        var tag = new Tag { OwnerId = ownerId, Name = name, NormalizedName = normalized };
        db.Tags.Add(tag);
        await db.SaveChangesAsync(cancellationToken);
        return tag.Id;
    }

    public async Task RenameTagAsync(Guid ownerId, Guid tagId, string name, CancellationToken cancellationToken = default)
    {
        name = CleanName(name, 80, "Tag");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tag = await RequireTagAsync(db, ownerId, tagId, cancellationToken);
        if (tag.Name == name) return;
        await RequireUniqueTagNameAsync(db, ownerId, name.ToUpperInvariant(), tagId, cancellationToken);
        tag.Name = name;
        tag.NormalizedName = name.ToUpperInvariant();
        await TouchTaggedActivitiesAsync(db, tagId, cancellationToken);
        var searches = await db.SavedSearches.Where(x => x.OwnerId == ownerId).ToListAsync(cancellationToken);
        foreach (var search in searches)
        {
            var criteria = TryReadCriteria(search.CriteriaJson);
            if (criteria is not null && criteria.Tags.Any(x => x.Id == tagId))
                search.CriteriaJson = JsonSerializer.Serialize(criteria with
                {
                    Tags = criteria.Tags.Select(x => x.Id == tagId ? new SavedTagReference(tagId, name) : x).ToArray()
                }, CriteriaJsonOptions);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTagAsync(Guid ownerId, Guid tagId, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tag = await RequireTagAsync(db, ownerId, tagId, cancellationToken);
        await TouchTaggedActivitiesAsync(db, tagId, cancellationToken);
        db.Tags.Remove(tag);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetActivityTagsAsync(Guid ownerId, Guid activityId, IReadOnlyList<Guid> tagIds, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var ids = ValidateIds(tagIds, "tags", allowEmpty: true);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        await ReadTagsAsync(db, ownerId, ids, cancellationToken);
        var activity = await db.Activities.Include(x => x.Tags).SingleOrDefaultAsync(x => x.Id == activityId && x.OwnerId == ownerId, cancellationToken);
        if (activity is null || activity.MutationVersion != expectedVersion) throw StaleSelection();
        if (!activity.Tags.Select(x => x.TagId).ToHashSet().SetEquals(ids))
        {
            var wanted = ids.ToHashSet();
            db.ActivityTags.RemoveRange(activity.Tags.Where(x => !wanted.Contains(x.TagId)));
            var existing = activity.Tags.Select(x => x.TagId).ToHashSet();
            foreach (var id in ids.Where(id => !existing.Contains(id))) activity.Tags.Add(new ActivityTag { ActivityId = activityId, TagId = id });
            Touch(activity);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SavedSearchSummary>> ListSavedSearchesAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        var searches = await db.SavedSearches.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var tags = await db.Tags.AsNoTracking().Where(x => x.OwnerId == ownerId).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var result = searches.Select(search =>
        {
            var criteria = TryReadCriteria(search.CriteriaJson);
            if (criteria is null) return new SavedSearchSummary(search.Id, search.Name, null, "This saved search has unsupported or damaged criteria. Replace it with the current filters or delete it.", []);
            var missing = criteria.Tags.Where(x => !tags.ContainsKey(x.Id)).ToArray();
            criteria = criteria with { Tags = criteria.Tags.Select(x => tags.TryGetValue(x.Id, out var name) ? x with { Name = name } : x).ToArray() };
            return new SavedSearchSummary(search.Id, search.Name, criteria, null, missing);
        }).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<Guid> SaveSavedSearchAsync(Guid ownerId, string name, SavedSearchCriteria criteria, Guid? id = null, CancellationToken cancellationToken = default)
    {
        name = CleanName(name, 120, "Saved search");
        criteria = ValidateCriteria(criteria);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        await RequireUniqueSearchNameAsync(db, ownerId, name.ToUpperInvariant(), id, cancellationToken);
        var tags = await ReadTagsAsync(db, ownerId, criteria.Tags.Select(x => x.Id).ToArray(), cancellationToken);
        criteria = criteria with { Tags = tags.Select(x => new SavedTagReference(x.Id, x.Name)).ToArray() };
        var search = id.HasValue
            ? await RequireSearchAsync(db, ownerId, id.Value, cancellationToken)
            : new SavedSearch { OwnerId = ownerId };
        search.Name = name;
        search.NormalizedName = name.ToUpperInvariant();
        search.CriteriaJson = JsonSerializer.Serialize(criteria, CriteriaJsonOptions);
        if (!id.HasValue) db.SavedSearches.Add(search);
        await db.SaveChangesAsync(cancellationToken);
        return search.Id;
    }

    public async Task RenameSavedSearchAsync(Guid ownerId, Guid id, string name, CancellationToken cancellationToken = default)
    {
        name = CleanName(name, 120, "Saved search");
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var search = await RequireSearchAsync(db, ownerId, id, cancellationToken);
        await RequireUniqueSearchNameAsync(db, ownerId, name.ToUpperInvariant(), id, cancellationToken);
        search.Name = name;
        search.NormalizedName = name.ToUpperInvariant();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSavedSearchAsync(Guid ownerId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.SavedSearches.Remove(await RequireSearchAsync(db, ownerId, id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BatchReview> PrepareBatchAsync(Guid ownerId, IReadOnlyCollection<Guid>? activityIds, ActivityFilter? filter, BatchChangeRequest changes, CancellationToken cancellationToken = default)
    {
        if ((activityIds is null) == (filter is null)) throw new ArgumentException("Choose selected activities or a filtered snapshot.");
        if (filter is not null && filter.OwnerId != ownerId) throw new ArgumentException("Select one profile before preparing a batch.", nameof(filter));
        changes = ValidateChanges(changes);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([ownerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await RequireOwnerAsync(db, ownerId, cancellationToken);
        var ids = ValidateIds(activityIds ?? await activities.GetMatchingActivityIdsAsync(filter!, cancellationToken), "activities");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var add = await ReadTagsAsync(db, ownerId, changes.AddTagIds, cancellationToken);
        var remove = await ReadTagsAsync(db, ownerId, changes.RemoveTagIds, cancellationToken);
        var members = await ReadMembersAsync(db, ownerId, ids, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BatchReview(ownerId, members, changes, add, remove);
    }

    public async Task<BatchEditResult> ApplyBatchAsync(BatchReview review, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(review.Members);
        if (review.Members.Any(x => x is null || x.MutationVersion < 0)) throw new ArgumentException("The batch review is invalid.", nameof(review));
        var ids = ValidateIds(review.Members.Select(x => x.Id).ToArray(), "activities");
        if (ids.Length != review.Members.Count) throw new ArgumentException("The batch review contains repeated activities.", nameof(review));
        var changes = ValidateChanges(review.Changes);
        await using var ownerLock = await ownerMutationLock.AcquireAsync([review.OwnerId], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RequireOwnerAsync(db, review.OwnerId, cancellationToken);
        var add = await ReadTagsAsync(db, review.OwnerId, changes.AddTagIds, cancellationToken);
        var remove = await ReadTagsAsync(db, review.OwnerId, changes.RemoveTagIds, cancellationToken);
        if (!SameReviewedTags(add, review.AddTags) || !SameReviewedTags(remove, review.RemoveTags)) throw StaleSelection();
        var members = await ReadMembersAsync(db, review.OwnerId, ids, cancellationToken);
        var versions = review.Members.ToDictionary(x => x.Id, x => x.MutationVersion);
        if (members.Any(x => versions[x.Id] != x.MutationVersion)) throw StaleSelection();
        var addIds = changes.AddTagIds.ToHashSet();
        var removeIds = changes.RemoveTagIds.ToHashSet();
        var changed = 0;
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            var batch = await db.Activities.Include(x => x.Tags).Where(x => chunk.Contains(x.Id) && x.OwnerId == review.OwnerId).ToListAsync(cancellationToken);
            foreach (var activity in batch)
            {
                var removed = activity.Tags.Where(x => removeIds.Contains(x.TagId)).ToArray();
                var existing = activity.Tags.Select(x => x.TagId).ToHashSet();
                var added = addIds.Where(id => !existing.Contains(id)).ToArray();
                var gear = changes.GearMode switch { GearEditMode.Set => changes.GearName, GearEditMode.Clear => null, _ => activity.GearName };
                var gearChanged = changes.GearMode != GearEditMode.Unchanged && (activity.GearName != gear || !activity.UserEdited);
                if (removed.Length == 0 && added.Length == 0 && !gearChanged) continue;
                db.ActivityTags.RemoveRange(removed);
                foreach (var id in added) activity.Tags.Add(new ActivityTag { ActivityId = activity.Id, TagId = id });
                if (changes.GearMode != GearEditMode.Unchanged)
                {
                    activity.GearName = gear;
                    activity.UserEdited = true;
                }
                Touch(activity);
                changed++;
            }
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync(cancellationToken);
        return new BatchEditResult(ids.Length, changed);
    }

    private static bool SameReviewedTags(IReadOnlyList<TagSummary> actual, IReadOnlyList<TagSummary>? reviewed) =>
        reviewed is not null && actual.Count == reviewed.Count &&
        actual.All(tag => reviewed.Any(x => x is not null && x.Id == tag.Id && x.Name == tag.Name));

    private static void Touch(Activity activity)
    {
        activity.MutationVersion++;
        activity.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static Task<int> TouchTaggedActivitiesAsync(ExplorerDbContext db, Guid tagId, CancellationToken cancellationToken) =>
        db.Activities.Where(x => x.Tags.Any(t => t.TagId == tagId)).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.MutationVersion, x => x.MutationVersion + 1)
            .SetProperty(x => x.UpdatedAtUtc, DateTimeOffset.UtcNow), cancellationToken);

    private static async Task<IReadOnlyList<BatchMember>> ReadMembersAsync(ExplorerDbContext db, Guid ownerId, Guid[] ids, CancellationToken cancellationToken)
    {
        var members = new List<BatchMember>(ids.Length);
        foreach (var chunk in ids.Chunk(ChunkSize))
            members.AddRange(await db.Activities.AsNoTracking().Where(x => x.OwnerId == ownerId && chunk.Contains(x.Id))
                .Select(x => new BatchMember(x.Id, x.MutationVersion)).ToListAsync(cancellationToken));
        if (members.Count != ids.Length) throw StaleSelection();
        return Array.AsReadOnly(members.ToArray());
    }

    private static async Task<IReadOnlyList<TagSummary>> ReadTagsAsync(ExplorerDbContext db, Guid ownerId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var tags = new List<TagSummary>();
        foreach (var chunk in ids.Distinct().Chunk(ChunkSize))
            tags.AddRange(await db.Tags.AsNoTracking().Where(x => x.OwnerId == ownerId && chunk.Contains(x.Id))
                .Select(x => new TagSummary(x.Id, x.Name, 0, 0)).ToListAsync(cancellationToken));
        if (tags.Count != ids.Distinct().Count()) throw new InvalidOperationException("One or more tags no longer belong to this profile. Refresh the tags and try again.");
        return Array.AsReadOnly(tags.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task RequireOwnerAsync(ExplorerDbContext db, Guid ownerId, CancellationToken cancellationToken)
    {
        if (ownerId == Guid.Empty || !await db.Owners.AnyAsync(x => x.Id == ownerId, cancellationToken))
            throw new InvalidOperationException("Select an existing profile first.");
    }

    private static async Task<Tag> RequireTagAsync(ExplorerDbContext db, Guid ownerId, Guid id, CancellationToken cancellationToken) =>
        await db.Tags.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, cancellationToken)
        ?? throw new InvalidOperationException("The tag was not found in this profile.");

    private static async Task<SavedSearch> RequireSearchAsync(ExplorerDbContext db, Guid ownerId, Guid id, CancellationToken cancellationToken) =>
        await db.SavedSearches.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, cancellationToken)
        ?? throw new InvalidOperationException("The saved search was not found in this profile.");

    private static async Task RequireUniqueTagNameAsync(ExplorerDbContext db, Guid ownerId, string normalized, Guid? except, CancellationToken cancellationToken)
    {
        if (await db.Tags.AnyAsync(x => x.OwnerId == ownerId && x.NormalizedName == normalized && x.Id != except, cancellationToken))
            throw new InvalidOperationException("A tag with that name already exists in this profile.");
    }

    private static async Task RequireUniqueSearchNameAsync(ExplorerDbContext db, Guid ownerId, string normalized, Guid? except, CancellationToken cancellationToken)
    {
        if (await db.SavedSearches.AnyAsync(x => x.OwnerId == ownerId && x.NormalizedName == normalized && x.Id != except, cancellationToken))
            throw new InvalidOperationException("A saved search with that name already exists in this profile.");
    }

    private static string CleanName(string? name, int maximum, string label)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > maximum)
            throw new ArgumentException($"{label} name must contain 1 to {maximum} characters.", nameof(name));
        return name;
    }

    private static Guid[] ValidateIds(IEnumerable<Guid>? ids, string label, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var result = ids.Distinct().ToArray();
        if (result.Any(x => x == Guid.Empty) || !allowEmpty && result.Length == 0)
            throw new ArgumentException($"Select valid {label}.", nameof(ids));
        return result;
    }

    private static BatchChangeRequest ValidateChanges(BatchChangeRequest changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var add = ValidateIds(changes.AddTagIds, "tags", allowEmpty: true);
        var remove = ValidateIds(changes.RemoveTagIds, "tags", allowEmpty: true);
        if (add.Intersect(remove).Any()) throw new ArgumentException("A tag cannot be added and removed in the same batch.", nameof(changes));
        if (!Enum.IsDefined(changes.GearMode)) throw new ArgumentException("Choose a valid gear change.", nameof(changes));
        var gear = changes.GearMode == GearEditMode.Set ? CleanName(changes.GearName, 160, "Gear") : null;
        if (add.Length == 0 && remove.Length == 0 && changes.GearMode == GearEditMode.Unchanged)
            throw new ArgumentException("Choose at least one tag or gear change.", nameof(changes));
        return new(Array.AsReadOnly(add), Array.AsReadOnly(remove), changes.GearMode, gear);
    }

    private static SavedSearchCriteria ValidateCriteria(SavedSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.Version != 1) throw new ArgumentException("This saved search version is unsupported.", nameof(criteria));
        if (criteria.Sport.HasValue && !Enum.IsDefined(criteria.Sport.Value)) throw new ArgumentException("Choose a valid sport.", nameof(criteria));
        if (criteria.Sort is not ("start-desc" or "start-asc" or "distance-desc" or "distance-asc" or "duration-desc" or "elevation-desc"))
            throw new ArgumentException("Choose a valid activity sort.", nameof(criteria));
        ArgumentNullException.ThrowIfNull(criteria.Dates);
        ReportingDates.Validate(criteria.Dates);
        ArgumentNullException.ThrowIfNull(criteria.Tags);
        if (criteria.Tags.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name) || x.Name.Trim().Length > 80))
            throw new ArgumentException("The saved tags are invalid.", nameof(criteria));
        var dates = criteria.Dates.Preset == ReportingPreset.Custom ? criteria.Dates : new ReportingDateSelection(criteria.Dates.Preset);
        return criteria with
        {
            Search = string.IsNullOrWhiteSpace(criteria.Search) ? null : criteria.Search.Trim(),
            Device = string.IsNullOrWhiteSpace(criteria.Device) ? null : criteria.Device.Trim(),
            Tags = criteria.Tags.DistinctBy(x => x.Id).Select(x => x with { Name = x.Name.Trim() }).ToArray(),
            Dates = dates
        };
    }

    private static SavedSearchCriteria? TryReadCriteria(string json)
    {
        try
        {
            var criteria = JsonSerializer.Deserialize<SavedSearchCriteria>(json, CriteriaJsonOptions);
            return criteria is null ? null : ValidateCriteria(criteria);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static InvalidOperationException StaleSelection() =>
        new("One or more reviewed activities or tags changed. Refresh the selection and review the changes again.");
}

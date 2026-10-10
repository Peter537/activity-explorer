using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IActivityOrganizationService
{
    Task<IReadOnlyList<TagSummary>> ListTagsAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Guid> CreateTagAsync(Guid ownerId, string name, CancellationToken cancellationToken = default);
    Task RenameTagAsync(Guid ownerId, Guid tagId, string name, CancellationToken cancellationToken = default);
    Task DeleteTagAsync(Guid ownerId, Guid tagId, CancellationToken cancellationToken = default);
    Task SetActivityTagsAsync(Guid ownerId, Guid activityId, IReadOnlyList<Guid> tagIds, long expectedVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedSearchSummary>> ListSavedSearchesAsync(Guid ownerId, CancellationToken cancellationToken = default);
    Task<Guid> SaveSavedSearchAsync(Guid ownerId, string name, SavedSearchCriteria criteria, Guid? id = null, CancellationToken cancellationToken = default);
    Task RenameSavedSearchAsync(Guid ownerId, Guid id, string name, CancellationToken cancellationToken = default);
    Task DeleteSavedSearchAsync(Guid ownerId, Guid id, CancellationToken cancellationToken = default);
    Task<BatchReview> PrepareBatchAsync(Guid ownerId, IReadOnlyCollection<Guid>? activityIds, ActivityFilter? filter, BatchChangeRequest changes, CancellationToken cancellationToken = default);
    Task<BatchEditResult> ApplyBatchAsync(BatchReview review, CancellationToken cancellationToken = default);
}

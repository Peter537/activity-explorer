using ActivityExplorer.Core.Models;

namespace ActivityExplorer.Core.Contracts;

public interface IBadgeService
{
    Task<IReadOnlyList<BadgeProfileOverview>> GetOverviewAsync(DateOnly? month = null, CancellationToken cancellationToken = default);
    Task<BadgeSnapshot> GetCatalogueAsync(Guid ownerId, DateOnly? month = null, CancellationToken cancellationToken = default);
    Task<BadgeDetail?> GetDetailAsync(Guid ownerId, string badgeId, string? edition, DateOnly? month = null, CancellationToken cancellationToken = default);
}

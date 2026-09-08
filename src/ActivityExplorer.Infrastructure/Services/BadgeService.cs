using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class BadgeService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider timeProvider) : IBadgeService
{
    public async Task<BadgeSnapshot> GetCatalogueAsync(Guid ownerId, DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var owner = await db.Owners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ownerId, cancellationToken)
            ?? throw new InvalidOperationException("This profile is unavailable. Choose another profile.");
        var activities = await db.Activities.AsNoTracking().Where(x => x.OwnerId == ownerId)
            .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id)
            .Select(x => new BadgeActivity(x.Id, x.Sport, x.StartTimeUtc, x.Title, x.DistanceMeters,
                x.MovingTimeSeconds, x.MovingTimeSource, x.ElevationGainMeters)).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BadgeEvaluator.Evaluate(owner.Id, owner.DisplayName, owner.TimeZoneId, activities, month, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<BadgeProfileOverview>> GetOverviewAsync(DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var owners = await db.Owners.AsNoTracking().OrderBy(x => x.DisplayName).Select(x => new { x.Id, x.DisplayName }).ToArrayAsync(cancellationToken);
        var result = new List<BadgeProfileOverview>();
        foreach (var owner in owners)
        {
            try
            {
                var snapshot = await GetCatalogueAsync(owner.Id, month, cancellationToken);
                result.Add(new(owner.Id, owner.DisplayName, snapshot.Level, snapshot.EarnedCount, snapshot.AsOf, null));
            }
            catch (InvalidOperationException exception)
            {
                result.Add(new(owner.Id, owner.DisplayName, null, 0, null, exception.Message));
            }
        }
        return result;
    }

    public async Task<BadgeDetail?> GetDetailAsync(Guid ownerId, string badgeId, string? edition, DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetCatalogueAsync(ownerId, month, cancellationToken);
        var selected = edition is null ? snapshot.ForMonth.FirstOrDefault(x => x.Definition.Id == badgeId) :
            snapshot.Editions.FirstOrDefault(x => x.Definition.Id == badgeId && x.Edition == edition);
        return selected is null ? null : new(snapshot, selected, snapshot.Editions.Where(x => x.Definition.FamilyId == selected.Definition.FamilyId).ToArray());
    }
}

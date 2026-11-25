using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class BadgeService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider timeProvider) : IBadgeService
{
    public async Task<BadgeSnapshot> GetCatalogueAsync(Guid ownerId, DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        var (name, timeZone, activities) = await CaptureAsync(ownerId, cancellationToken);
        return BadgeEvaluator.Evaluate(ownerId, name, timeZone, activities, month, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<BadgeProfileOverview>> GetOverviewAsync(DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        (Guid Id, string Name)[] owners;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var rows = await db.Owners.AsNoTracking().OrderBy(x => x.DisplayName).Select(x => new { x.Id, x.DisplayName }).ToArrayAsync(cancellationToken);
            owners = rows.Select(x => (x.Id, x.DisplayName)).ToArray();
        }
        var now = timeProvider.GetUtcNow();
        var result = new List<BadgeProfileOverview>();
        foreach (var owner in owners)
        {
            try
            {
                var (name, timeZone, activities) = await CaptureAsync(owner.Id, cancellationToken);
                result.Add(BadgeEvaluator.EvaluateOverview(owner.Id, name, timeZone, activities, month, now, cancellationToken));
            }
            catch (InvalidOperationException exception)
            {
                result.Add(new(owner.Id, owner.Name, null, 0, null, exception.Message));
            }
        }
        return result;
    }

    public async Task<BadgeDetail?> GetDetailAsync(Guid ownerId, string badgeId, string? edition, DateOnly? month = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetCatalogueAsync(ownerId, month, cancellationToken);
        var selected = snapshot.FindEdition(badgeId, edition);
        return selected is null ? null : new(snapshot, selected);
    }

    private async Task<(string Name, string? TimeZone, BadgeActivity[] Activities)> CaptureAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var owner = await db.Owners.AsNoTracking().Where(x => x.Id == ownerId).Select(x => new { x.DisplayName, x.TimeZoneId })
            .SingleOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("This profile is unavailable. Choose another profile.");
        var activities = await db.Activities.AsNoTracking().Where(x => x.OwnerId == ownerId)
            .OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id)
            .Select(x => new BadgeActivity(x.Id, x.Sport, x.StartTimeUtc, x.Title, x.DistanceMeters,
                x.MovingTimeSeconds, x.MovingTimeSource, x.ElevationGainMeters)).ToArrayAsync(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);
        return (owner.DisplayName, owner.TimeZoneId, activities);
    }
}

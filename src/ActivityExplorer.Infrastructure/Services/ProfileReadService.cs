using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ProfileReadService(IDbContextFactory<ExplorerDbContext> factory) : IProfileReadService
{
    public async Task<bool> ExistsAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Owners.AnyAsync(owner => owner.Id == ownerId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProfileSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await Project(db.Owners.AsNoTracking(), db).ToListAsync(cancellationToken);
    }

    public async Task<PagedResult<ProfileSummary>> SearchAsync(ReadPage page, IReadOnlyList<Guid>? allowedProfiles = null, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var query = db.Owners.AsNoTracking();
        if (allowedProfiles is not null) query = query.Where(profile => allowedProfiles.Contains(profile.Id));
        var count = await query.CountAsync(cancellationToken);
        var items = await Project(query.OrderBy(profile => profile.DisplayName).ThenBy(profile => profile.Id).Skip(offset).Take(page.PageSize), db).ToArrayAsync(cancellationToken);
        return new(items, count, page.Page, page.PageSize);
    }

    private static IQueryable<ProfileSummary> Project(IQueryable<OwnerProfile> source, ExplorerDbContext db) => source
        .OrderBy(owner => owner.DisplayName).ThenBy(owner => owner.Id)
        .Select(owner => new ProfileSummary(owner.Id, owner.DisplayName,
            db.Activities.Count(activity => activity.OwnerId == owner.Id),
            db.Activities.Where(activity => activity.OwnerId == owner.Id).Sum(activity => activity.DistanceMeters),
            owner.CreatedAtUtc, owner.TimeZoneId));
}

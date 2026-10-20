using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class RouteReadService(IDbContextFactory<ExplorerDbContext> factory) : IRouteReadService
{
    public async Task<IReadOnlyList<RouteSummary>> SearchAsync(RouteFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await ImportBatchQuery.ValidateAsync(db, filter.OwnerId, filter.ImportBatchId, cancellationToken);
        return await Query(db, filter).ToArrayAsync(cancellationToken);
    }

    public async Task<PagedResult<RouteSummary>> SearchAsync(RouteFilter filter, ReadPage page, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        await ImportBatchQuery.ValidateAsync(db, filter.OwnerId, filter.ImportBatchId, cancellationToken);
        var query = Query(db, filter);
        var count = await query.CountAsync(cancellationToken);
        return new(await query.Skip(offset).Take(page.PageSize).ToArrayAsync(cancellationToken), count, page.Page, page.PageSize);
    }

    public async Task<RouteReadDetail?> GetAsync(Guid ownerId, Guid routeId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Routes.AsNoTracking().Where(route => route.OwnerId == ownerId && route.Id == routeId)
            .Select(route => new RouteReadDetail(new RouteSummary(route.Id, route.OwnerId, route.Owner!.DisplayName,
                route.Name, route.Sport, route.DistanceMeters, route.ElevationGainMeters), route.Description)).SingleOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<RouteSummary> Query(ExplorerDbContext db, RouteFilter filter)
    {
        if (filter.Sport.HasValue && !Enum.IsDefined(filter.Sport.Value)) throw new ArgumentException("Choose a valid sport.");
        var query = db.Routes.AsNoTracking().Where(route => !filter.OwnerId.HasValue || route.OwnerId == filter.OwnerId);
        if (filter.Sport.HasValue) query = query.Where(route => route.Sport == filter.Sport);
        if (filter.ImportBatchId.HasValue) query = query.Where(route => route.SourceFiles.Any(source => source.ImportBatchId == filter.ImportBatchId && source.OwnerId == route.OwnerId));
        if (!string.IsNullOrWhiteSpace(filter.Search)) query = query.Where(route => ActivitySearchFunctions.Contains(route.Name, filter.Search.Trim()));
        var ordered = filter.Sort switch
        {
            "name-asc" => query.OrderBy(route => route.Name),
            "name-desc" => query.OrderByDescending(route => route.Name),
            "distance-asc" => query.OrderBy(route => route.DistanceMeters).ThenBy(route => route.Name),
            "distance-desc" => query.OrderByDescending(route => route.DistanceMeters).ThenBy(route => route.Name),
            "elevation-asc" => query.OrderBy(route => route.ElevationGainMeters).ThenBy(route => route.Name),
            "elevation-desc" => query.OrderByDescending(route => route.ElevationGainMeters).ThenBy(route => route.Name),
            _ => throw new ArgumentException("Choose a valid route order.")
        };
        return ordered.ThenBy(route => route.Id).Select(route => new RouteSummary(route.Id, route.OwnerId,
            route.Owner!.DisplayName, route.Name, route.Sport, route.DistanceMeters, route.ElevationGainMeters));
    }
}

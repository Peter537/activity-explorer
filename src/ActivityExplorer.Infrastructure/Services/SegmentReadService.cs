using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class SegmentReadService(IDbContextFactory<ExplorerDbContext> contextFactory) : ISegmentReadService
{
    public async Task<IReadOnlyList<SegmentSummary>> ListAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = db.Segments.AsNoTracking();
        if (ownerId.HasValue) source = source.Where(x => x.OwnerId == ownerId);
        return await Summaries(source.OrderBy(x => x.Name).ThenBy(x => x.Id), db).ToArrayAsync(cancellationToken);
    }

}

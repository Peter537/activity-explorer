using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class ActivityReadService(IDbContextFactory<ExplorerDbContext> contextFactory, TimeProvider? timeProvider = null) : IActivityReadService
{
    public async Task<ActivityFilterOptions> GetFilterOptionsAsync(Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var names = await db.Activities.AsNoTracking()
            .Where(x => !ownerId.HasValue || x.OwnerId == ownerId)
            .Select(x => new { x.DeviceName, x.GearName }).Distinct().ToArrayAsync(cancellationToken);
        static string[] Options(IEnumerable<string?> values) => values.Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim()).Order(StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(Options(names.Select(x => x.DeviceName)), Options(names.Select(x => x.GearName)));
    }

    public async Task<ActivitySearchResult> SearchAsync(ActivityFilter filter, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var effective = await ResolveFilterAsync(db, filter, cancellationToken);
        var query = ApplyFilter(db.Activities.AsNoTracking(), effective);
        var totals = await query.GroupBy(x => 1).Select(group => new ActivityTotals(
            group.Count(), group.Sum(x => x.DistanceMeters), group.Sum(x => x.MovingTimeSeconds), group.Sum(x => x.ElevationGainMeters)))
            .SingleOrDefaultAsync(cancellationToken) ?? new ActivityTotals(0, 0, 0, 0);
        var page = Math.Max(filter.Page, 1);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);
        var activities = await ApplySort(query.Include(x => x.Owner), filter.Sort)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(activity => new ActivitySummary(activity.Id, activity.OwnerId, activity.Owner!.DisplayName,
                activity.Title, activity.Sport, activity.StartTimeUtc, activity.DistanceMeters, activity.MovingTimeSeconds,
                activity.ElevationGainMeters, activity.AveragePowerWatts, activity.DeviceName, activity.HasGps, activity.HasPower)
            { MutationVersion = activity.MutationVersion }).ToListAsync(cancellationToken);
        var activityIds = activities.Select(x => x.Id).ToArray();
        var tags = await db.ActivityTags.AsNoTracking().Include(x => x.Tag)
            .Where(x => activityIds.Contains(x.ActivityId) && x.Tag!.OwnerId == x.Activity!.OwnerId).ToListAsync(cancellationToken);
        var tagsByActivity = tags.ToLookup(x => x.ActivityId);
        for (var index = 0; index < activities.Count; index++)
            activities[index] = activities[index] with
            {
                Tags = tagsByActivity[activities[index].Id].OrderBy(tag => tag.Tag!.Name).ThenBy(tag => tag.TagId)
                    .Select(tag => new TagSummary(tag.TagId, tag.Tag!.Name)).ToArray()
            };
        await transaction.CommitAsync(cancellationToken);
        return new(activities.ToArray(), totals.ActivityCount, page, pageSize, totals,
            effective with { Page = page, PageSize = pageSize });
    }

    public async Task<IReadOnlyList<Guid>> GetMatchingActivityIdsAsync(
        ActivityFilter filter,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var effective = await ResolveFilterAsync(db, filter, cancellationToken);
        var ids = await ApplySort(ApplyFilter(db.Activities.AsNoTracking(), effective), filter.Sort)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    public async Task<IReadOnlyDictionary<SportKind, int>> GetSportActivityCountsAsync(
        Guid? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Activities.AsNoTracking().AsQueryable();
        if (ownerId.HasValue) query = query.Where(x => x.OwnerId == ownerId);
        return await query.GroupBy(x => x.Sport)
            .Select(group => new { Sport = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Sport, x => x.Count, cancellationToken);
    }

    private async Task<ActivityFilter> ResolveFilterAsync(ExplorerDbContext db, ActivityFilter filter, CancellationToken cancellationToken)
    {
        await ImportBatchQuery.ValidateAsync(db, filter.OwnerId, filter.ImportBatchId, cancellationToken);
        var criteria = await ActivityCriteriaQuery.ResolveAsync(db, filter.OwnerId, filter.GetCriteria(), cancellationToken);
        var selection = ReportingDateQuery.Selection(filter.Period, filter.From, filter.To);
        ReportingDates.Validate(selection);
        var asOf = filter.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();
        var periods = filter.ResolvedPeriods ?? await ReportingDateQuery.ResolveAsync(db, filter.OwnerId, selection, asOf, cancellationToken);
        return filter with
        {
            Period = selection.Preset,
            From = selection.Preset == ReportingPreset.Custom ? selection.From : null,
            To = selection.Preset == ReportingPreset.Custom ? selection.To : null,
            AsOfUtc = asOf,
            ResolvedPeriods = periods,
            Sport = criteria.Sport,
            Search = criteria.Search,
            HasPower = criteria.HasPower,
            Device = criteria.Device,
            TagIds = criteria.TagIds,
            Criteria = criteria
        };
    }

    private static IQueryable<Activity> ApplyFilter(IQueryable<Activity> query, ActivityFilter filter)
    {
        if (filter.OwnerId.HasValue) query = query.Where(x => x.OwnerId == filter.OwnerId);
        if (filter.ImportBatchId.HasValue)
            query = query.Where(activity => activity.SourceFiles.Any(source => source.ImportBatchId == filter.ImportBatchId &&
                source.OwnerId == activity.OwnerId));
        query = ReportingDateQuery.Apply(query, filter.ResolvedPeriods!);
        return ActivityCriteriaQuery.Apply(query, filter.GetCriteria());
    }

    private static IQueryable<Activity> ApplySort(IQueryable<Activity> query, string sort) => sort switch
    {
        "start-asc" => query.OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Id),
        "distance-desc" => query.OrderByDescending(x => x.DistanceMeters).ThenBy(x => x.Id),
        "distance-asc" => query.OrderBy(x => x.DistanceMeters).ThenBy(x => x.Id),
        "duration-desc" => query.OrderByDescending(x => x.MovingTimeSeconds).ThenBy(x => x.Id),
        "elevation-desc" => query.OrderByDescending(x => x.ElevationGainMeters).ThenBy(x => x.Id),
        _ => query.OrderByDescending(x => x.StartTimeUtc).ThenBy(x => x.Id)
    };

    internal static ActivitySummary ToSummary(Activity x) => new(
        x.Id, x.OwnerId, x.Owner?.DisplayName ?? "Unknown profile", x.Title, x.Sport, x.StartTimeUtc,
        x.DistanceMeters, x.MovingTimeSeconds, x.ElevationGainMeters, x.AveragePowerWatts,
        x.DeviceName, x.HasGps, x.HasPower)
    {
        Tags = x.Tags.OrderBy(tag => tag.Tag!.Name).ThenBy(tag => tag.TagId)
            .Select(tag => new TagSummary(tag.TagId, tag.Tag!.Name)).ToArray(),
        MutationVersion = x.MutationVersion
    };
}

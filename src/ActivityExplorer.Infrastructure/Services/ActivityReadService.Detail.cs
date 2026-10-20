using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class ActivityReadService
{
    public async Task<ActivityReadDetail?> GetDetailAsync(Guid ownerId, Guid activityId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        var detail = await db.Activities.AsNoTracking().Where(row => row.Id == activityId && row.OwnerId == ownerId)
            .Select(activity => new ActivityReadDetail(new ActivitySummary(activity.Id, activity.OwnerId, activity.Owner!.DisplayName,
                activity.Title, activity.Sport, activity.StartTimeUtc, activity.DistanceMeters, activity.MovingTimeSeconds,
                activity.ElevationGainMeters, activity.AveragePowerWatts, activity.DeviceName, activity.HasGps, activity.HasPower)
            { MutationVersion = activity.MutationVersion },
                activity.Description, activity.GearName, activity.ElapsedTimeSeconds, activity.Calories,
                activity.AverageSpeedMetersPerSecond, activity.MaxSpeedMetersPerSecond, activity.AverageHeartRate,
                activity.MaxHeartRate, activity.AverageCadence, activity.MaxPowerWatts, activity.NormalizedPowerWatts,
                db.ActivityLaps.Count(row => row.ActivityId == activityId), db.SourceFiles.Count(row => row.ActivityId == activityId),
                db.SegmentEfforts.Count(row => row.ActivityId == activityId && row.OwnerId == ownerId),
                db.ActivityMetrics.Count(row => row.ActivityId == activityId)))
            .SingleOrDefaultAsync(cancellationToken);
        if (detail is null) return null;
        var summary = detail.Summary with
        {
            Tags = await db.ActivityTags.AsNoTracking().Where(tag => tag.ActivityId == activityId && tag.Tag!.OwnerId == ownerId)
                .OrderBy(tag => tag.Tag!.Name).ThenBy(tag => tag.TagId)
                .Select(tag => new TagSummary(tag.TagId, tag.Tag!.Name)).ToArrayAsync(cancellationToken)
        };
        return detail with { Summary = summary };
    }

    public async Task<PagedResult<FilterValue>> ListFilterValuesAsync(Guid ownerId, string kind, string? search, ReadPage page, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var snapshot = await ReadSnapshot.BeginAsync(db, cancellationToken);
        IQueryable<FilterValue> query;
        if (kind == "tag")
        {
            var tags = db.Tags.AsNoTracking().Where(tag => tag.OwnerId == ownerId);
            if (!string.IsNullOrWhiteSpace(search)) tags = tags.Where(tag => ActivitySearchFunctions.Contains(tag.Name, search));
            query = tags.OrderBy(tag => tag.Name).ThenBy(tag => tag.Id).Select(tag => new FilterValue(tag.Id, tag.Name));
        }
        else
        {
            var activities = db.Activities.AsNoTracking().Where(activity => activity.OwnerId == ownerId);
            var values = kind switch
            {
                "device" => activities.Select(activity => activity.DeviceName),
                "gear" => activities.Select(activity => activity.GearName),
                _ => throw new ArgumentException("Choose device, gear, or tag filter values.")
            };
            values = values.Where(value => value != null && value.Trim() != "");
            if (!string.IsNullOrWhiteSpace(search)) values = values.Where(value => ActivitySearchFunctions.Contains(value, search));
            query = values.Distinct().OrderBy(value => value).Select(value => new FilterValue(null, value!));
        }
        var count = await query.CountAsync(cancellationToken);
        return new(await query.Skip(offset).Take(page.PageSize).ToArrayAsync(cancellationToken), count, page.Page, page.PageSize);
    }
}

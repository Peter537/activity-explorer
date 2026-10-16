using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

/// <summary>Shared SQL membership rules for activity lists, maps, reporting, and bulk selection.</summary>
internal static class ActivityCriteriaQuery
{
    public static async Task<ActivityCriteria> ResolveAsync(ExplorerDbContext db, Guid? ownerId,
        ActivityCriteria criteria, CancellationToken cancellationToken)
    {
        var normalized = ActivityCriteriaValidation.Normalize(criteria);
        var tagIds = normalized.TagIds!.Concat(normalized.ExcludedTagIds!).Distinct().ToArray();
        if (tagIds.Length == 0) return normalized;
        if (!ownerId.HasValue)
            throw new ArgumentException("Select one profile to filter by tags.", nameof(ownerId));
        var validCount = await db.Tags.CountAsync(x => x.OwnerId == ownerId && tagIds.Contains(x.Id), cancellationToken);
        if (validCount != tagIds.Length)
            throw new InvalidOperationException("One or more selected tags were deleted or belong to another profile. Remove or replace them before applying this search.");
        return normalized;
    }

    public static IQueryable<Activity> Apply(IQueryable<Activity> query, ActivityCriteria criteria)
    {
        if (criteria.Sport.HasValue) query = query.Where(x => x.Sport == criteria.Sport);
        if (criteria.HasPower.HasValue) query = query.Where(x => x.HasPower == criteria.HasPower);
        if (criteria.IsIndoor.HasValue) query = query.Where(x => x.IsIndoor == criteria.IsIndoor);
        if (criteria.HasGps.HasValue) query = query.Where(x => x.HasGps == criteria.HasGps);
        if (criteria.MinDistanceMeters.HasValue) query = query.Where(x => x.DistanceMeters >= criteria.MinDistanceMeters);
        if (criteria.MaxDistanceMeters.HasValue) query = query.Where(x => x.DistanceMeters <= criteria.MaxDistanceMeters);
        if (criteria.MinMovingSeconds.HasValue) query = query.Where(x => x.MovingTimeSeconds >= criteria.MinMovingSeconds);
        if (criteria.MaxMovingSeconds.HasValue) query = query.Where(x => x.MovingTimeSeconds <= criteria.MaxMovingSeconds);
        if (criteria.MinAscentMeters.HasValue) query = query.Where(x => x.ElevationGainMeters >= criteria.MinAscentMeters);
        if (criteria.MaxAscentMeters.HasValue) query = query.Where(x => x.ElevationGainMeters <= criteria.MaxAscentMeters);
        if (criteria.Device is { } device)
            query = criteria.DeviceExact
                ? query.Where(x => ActivitySearchFunctions.EqualsName(x.DeviceName, device))
                : query.Where(x => x.DeviceName != null && x.DeviceName.Contains(device));
        if (criteria.Gear is { } gear) query = query.Where(x => ActivitySearchFunctions.EqualsName(x.GearName, gear));
        if (criteria.GearUnassigned) query = query.Where(x => x.GearName == null || x.GearName.Trim() == "");
        if (criteria.Search is { } search)
        {
            if (criteria.TextMode == ActivityTextMode.Literal)
                query = query.Where(x => x.Title.Contains(search) ||
                    (x.Description != null && x.Description.Contains(search)));
            else
                foreach (var term in ActivityCriteriaValidation.ParseTextTerms(search))
                    query = query.Where(x => ActivitySearchFunctions.Contains(x.Title, term) ||
                        ActivitySearchFunctions.Contains(x.Description, term));
        }
        if (criteria.TagIds is { Count: > 0 })
        {
            var included = criteria.TagIds.ToArray();
            query = criteria.TagMode == ActivityTagMode.All
                ? query.Where(x => x.Tags.Count(tag => included.Contains(tag.TagId)) == included.Length)
                : query.Where(x => x.Tags.Any(tag => included.Contains(tag.TagId)));
        }
        if (criteria.ExcludedTagIds is { Count: > 0 })
        {
            var excluded = criteria.ExcludedTagIds.ToArray();
            query = query.Where(x => !x.Tags.Any(tag => excluded.Contains(tag.TagId)));
        }
        if (criteria.TagPresence == ActivityTagPresence.Tagged) query = query.Where(x => x.Tags.Any());
        if (criteria.TagPresence == ActivityTagPresence.Untagged) query = query.Where(x => !x.Tags.Any());
        return query;
    }
}

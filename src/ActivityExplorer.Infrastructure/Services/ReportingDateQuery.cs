using System.Linq.Expressions;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

internal static class ReportingDateQuery
{
    public static ReportingDateSelection Selection(ReportingPreset? preset, DateOnly? from, DateOnly? to) =>
        new(preset ?? (from.HasValue || to.HasValue ? ReportingPreset.Custom : ReportingPreset.AllTime), from, to);

    public static async Task<IReadOnlyList<ResolvedOwnerPeriod>> ResolveAsync(
        ExplorerDbContext db, Guid? ownerId, ReportingDateSelection selection, DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        ReportingDates.Validate(selection);
        var owners = await db.Owners.AsNoTracking().Where(x => !ownerId.HasValue || x.Id == ownerId)
            .OrderBy(x => x.DisplayName).Select(x => new { x.Id, x.DisplayName, x.TimeZoneId })
            .ToArrayAsync(cancellationToken);
        return owners.Select(owner =>
        {
            try { return ReportingDates.Resolve(selection, owner.Id, owner.DisplayName, owner.TimeZoneId, asOfUtc); }
            catch (InvalidOperationException exception)
            {
                throw new ArgumentException($"The reporting timezone for {owner.DisplayName} is unavailable. Choose a valid timezone in Profiles.", exception);
            }
        }).ToArray();
    }

    public static IQueryable<Activity> Apply(IQueryable<Activity> query, IReadOnlyList<ResolvedOwnerPeriod> periods)
    {
        var activity = Expression.Parameter(typeof(Activity), "activity");
        Expression predicate = Expression.Constant(false);
        foreach (var group in periods.GroupBy(x => (x.FromUtc, x.ToUtc)))
        {
            var owners = group.Select(x => x.OwnerId).ToArray();
            Expression member = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)],
                Expression.Constant(owners), Expression.Property(activity, nameof(Activity.OwnerId)));
            if (group.Key.FromUtc is { } from)
                member = Expression.AndAlso(member, Expression.GreaterThanOrEqual(
                    Expression.Property(activity, nameof(Activity.StartTimeUtc)), Expression.Constant(from)));
            if (group.Key.ToUtc is { } to)
                member = Expression.AndAlso(member, Expression.LessThan(
                    Expression.Property(activity, nameof(Activity.StartTimeUtc)), Expression.Constant(to)));
            predicate = Expression.OrElse(predicate, member);
        }
        return query.Where(Expression.Lambda<Func<Activity, bool>>(predicate, activity));
    }
}

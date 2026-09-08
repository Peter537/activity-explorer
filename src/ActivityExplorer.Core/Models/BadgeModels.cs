using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public enum BadgePeriod { Lifetime, Monthly, Quarterly, Annual, SpecialDate }
public enum BadgeMeasure { Distance, SingleDistance, Ascent, SingleAscent, MovingTime, ActivityCount, ActiveDays, Variety, ConsistentWeeks, Morning, Night, SpecialDate }
public enum BadgeStatus { Completed, InProgress, Incomplete, NotStarted, Upcoming }

public sealed record BadgeDefinition(
    string Id, string FamilyId, string Name, string FamilyName, SportKind? Sport,
    BadgePeriod Period, BadgeMeasure Measure, double Target, string Unit, int Points,
    string Artwork, string Requirement, int? CalendarMonth = null, int? CalendarDay = null);

public sealed record BadgeActivity(
    Guid Id, SportKind Sport, DateTimeOffset StartTimeUtc, string Title,
    double DistanceMeters, double MovingSeconds, MovingTimeSource MovingTimeSource, double AscentMeters);

public sealed record BadgeEvidence(Guid ActivityId, string Title, DateOnly Date, double Contribution);

public sealed record BadgeEdition(
    BadgeDefinition Definition, string Edition, DateOnly? StartsOn, DateOnly? EndsOn,
    double Progress, BadgeStatus Status, DateOnly? EarnedOn, IReadOnlyList<BadgeEvidence> Evidence)
{
    public double Percent => Math.Clamp(Progress / Definition.Target * 100, 0, 100);
    public string Identity(Guid ownerId) => $"{ownerId:N}:{Definition.Id}:{Edition}";
}

public sealed record BadgeLevel(long Points, long Level, long LevelStartPoints, long NextLevelPoints)
{
    public long EarnedInLevel => Points - LevelStartPoints;
    public long RequiredInLevel => NextLevelPoints - LevelStartPoints;
    public static BadgeLevel FromPoints(long points)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(points);
        var index = (long)Math.Sqrt(points / 5d);
        while (Threshold(index + 1) <= points) index++;
        while (Threshold(index) > points) index--;
        return new(points, index + 1, Threshold(index), Threshold(index + 1));
    }

    private static long Threshold(long index) => checked(5 * index * index);
}

public sealed record BadgeSnapshot(
    Guid OwnerId, string OwnerName, string TimeZoneId, int FirstYear, DateOnly Month,
    DateOnly AsOf, DateOnly Today, IReadOnlyList<BadgeEdition> Editions)
{
    public BadgeLevel Level => BadgeLevel.FromPoints(Editions.Where(x => x.Status == BadgeStatus.Completed).Sum(x => (long)x.Definition.Points));
    public int EarnedCount => Editions.Count(x => x.Status == BadgeStatus.Completed);
    public IReadOnlyList<BadgeEdition> ForMonth => Editions.Where(x => x.StartsOn is null ||
        x.StartsOn <= Month.AddMonths(1).AddDays(-1) && x.EndsOn >= Month).ToArray();
}

public sealed record BadgeProfileOverview(Guid OwnerId, string Name, BadgeLevel? Level, int EarnedCount, DateOnly? AsOf, string? Error);
public sealed record BadgeDetail(BadgeSnapshot Snapshot, BadgeEdition Selected, IReadOnlyList<BadgeEdition> Related);

public static class BadgeTimeZone
{
    public const string DefaultId = "Europe/Copenhagen";
    public static TimeZoneInfo Resolve(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? DefaultId : id); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("The profile's badge timezone is unavailable. Choose a valid timezone in Profiles.", exception);
        }
    }
}

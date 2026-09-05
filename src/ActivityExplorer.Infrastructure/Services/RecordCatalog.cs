using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Infrastructure.Services;

internal readonly record struct RecordTarget(string Key, double Target, int DisplayOrder);

internal static class RecordCatalog
{
    public const int ComputationVersion = 7;

    private static readonly RecordTarget[] RowingDistances =
    [
        new("100 m", 100, 0),
        new("500 m", 500, 1),
        new("1 km", 1_000, 2),
        new("2 km", 2_000, 3),
        new("5 km", 5_000, 4),
        new("6 km", 6_000, 5),
        new("10 km", 10_000, 6),
        new("Half marathon", 21_097, 7),
        new("Marathon", 42_195, 8)
    ];

    private static readonly RecordTarget[] RowingTimes =
    [
        new("1 min", 60, 0),
        new("4 min", 240, 1),
        new("30 min", 1_800, 2),
        new("1 hour", 3_600, 3)
    ];

    private static readonly RecordTarget[] RunningAndWalkingDistances =
    [
        new("400 m", 400, 0),
        new("1 km", 1_000, 1),
        new("1/2 mile", 804.672, 2),
        new("1 mile", 1_609.344, 3),
        new("2 miles", 3_218.688, 4),
        new("5 km", 5_000, 5),
        new("10 km", 10_000, 6),
        new("15 km", 15_000, 7),
        new("10 miles", 16_093.44, 8),
        new("20 km", 20_000, 9),
        new("Half marathon", 21_097.5, 10),
        new("30 km", 30_000, 11),
        new("Marathon", 42_195, 12),
        new("50 km", 50_000, 13)
    ];

    private static readonly RecordTarget[] CyclingDistances =
    [
        new("5 km", 5_000, 0),
        new("5 miles", 8_046.72, 1),
        new("10 km", 10_000, 2),
        new("10 miles", 16_093.44, 3),
        new("20 km", 20_000, 4),
        new("30 km", 30_000, 5),
        new("40 km", 40_000, 6),
        new("50 km", 50_000, 7),
        new("80 km", 80_000, 8),
        new("50 miles", 80_467.2, 9),
        new("90 km", 90_000, 10),
        new("100 km", 100_000, 11),
        new("100 miles", 160_934.4, 12),
        new("180 km", 180_000, 13),
        new("200 km", 200_000, 14)
    ];

    private static readonly RecordTarget[] RunningAndWalkingTimes =
    [
        new("5 min", 300, 0),
        new("10 min", 600, 1),
        new("15 min", 900, 2),
        new("30 min", 1_800, 3),
        new("1 hour", 3_600, 4),
        new("2 hours", 7_200, 5)
    ];

    private static readonly RecordTarget[] CyclingTimes =
    [
        new("5 min", 300, 0),
        new("10 min", 600, 1),
        new("20 min", 1_200, 2),
        new("30 min", 1_800, 3),
        new("1 hour", 3_600, 4),
        new("2 hours", 7_200, 5),
        new("4 hours", 14_400, 6)
    ];

    public static IReadOnlyList<RecordTarget> PowerTargets { get; } =
    [
        new("5 s", 5, 0),
        new("15 s", 15, 1),
        new("30 s", 30, 2),
        new("1 min", 60, 3),
        new("2 min", 120, 4),
        new("3 min", 180, 5),
        new("5 min", 300, 6),
        new("8 min", 480, 7),
        new("10 min", 600, 8),
        new("15 min", 900, 9),
        new("20 min", 1_200, 10),
        new("30 min", 1_800, 11),
        new("45 min", 2_700, 12),
        new("1 hour", 3_600, 13),
        new("2 hours", 7_200, 14)
    ];

    public static IReadOnlyList<RecordTarget> DistanceTargets(SportKind sport) => sport switch
    {
        SportKind.Cycling => CyclingDistances,
        SportKind.Running or SportKind.Walking => RunningAndWalkingDistances,
        SportKind.Rowing => RowingDistances,
        _ => []
    };

    public static IReadOnlyList<RecordTarget> TimedDistanceTargets(SportKind sport) => sport switch
    {
        SportKind.Cycling => CyclingTimes,
        SportKind.Running or SportKind.Walking => RunningAndWalkingTimes,
        SportKind.Rowing => RowingTimes,
        _ => []
    };

    public static int CategoryOrder(RecordKind kind) => kind switch
    {
        RecordKind.Distance => 0,
        RecordKind.Duration => 1,
        RecordKind.Elevation => 2,
        RecordKind.AverageSpeed => 3,
        RecordKind.DistanceEffort => 4,
        RecordKind.TimedDistanceEffort => 5,
        RecordKind.PowerCurve => 6,
        _ => int.MaxValue
    };

    public static int TargetOrder(SportKind sport, RecordKind kind, string key)
    {
        var targets = kind switch
        {
            RecordKind.DistanceEffort => DistanceTargets(sport),
            RecordKind.TimedDistanceEffort => TimedDistanceTargets(sport),
            RecordKind.PowerCurve => PowerTargets,
            _ => []
        };
        var target = targets.FirstOrDefault(candidate => candidate.Key == key);
        return target.Key is null ? int.MaxValue : target.DisplayOrder;
    }
}

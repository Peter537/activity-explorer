using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record RecordHistoryQuery(
    SportKind Sport, RecordKind Kind, string Key, Guid? OwnerId = null,
    RecordScope Scope = RecordScope.All, ReportingDateSelection? Period = null, DateTimeOffset? AsOfUtc = null);

public sealed record RecordHistoryPoint(RecordAttempt Attempt, DateOnly ReportingDate, double BestSoFar);

public sealed record RecordHistorySeries(
    Guid OwnerId, string OwnerName, string TimeZoneId, IReadOnlyList<RecordHistoryPoint> Points);

public sealed record RecordHistory(
    RecordBenchmark Benchmark, IReadOnlyList<RecordHistorySeries> Series, IReadOnlyList<ResolvedOwnerPeriod> Periods);

using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record RecordOverviewQuery(Guid? OwnerId = null, SportKind? Sport = null,
    RecordScope Scope = RecordScope.All, ReportingDateSelection? Period = null, DateTimeOffset? AsOfUtc = null);

public sealed record RecordOverviewResult(IReadOnlyList<PersonalRecord> Records, IReadOnlyList<ResolvedOwnerPeriod> Periods);

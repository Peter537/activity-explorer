using ActivityExplorer.Core.Domain;

namespace ActivityExplorer.Core.Models;

public sealed record ExplorationScope(Guid? OwnerId = null, SportKind? Sport = null);
public sealed record ExplorationQuery(
    ExplorationScope Scope, ReportingDateSelection Dates, DateTimeOffset AsOfUtc,
    bool Cumulative = false, DateOnly? Through = null, DateOnly? HistoryMonth = null, int Page = 1);
public sealed record ExplorationIndexStatus(int TotalActivities, int IndexedActivities, int LimitedActivities)
{
    public bool IsComplete => TotalActivities == IndexedActivities;
}
public sealed record ExplorationBuildProgress(int Completed, int Total, string Message);
public sealed record ExplorationSummary(
    int VisitedCells, int NewCells, int MatchingActivities, int MaximumFrequency,
    DateOnly? FirstDate, DateOnly? LastDate, DateOnly? Through, int LimitedActivities);
public sealed record ExplorationCell(int CellId, int X, int Y, int ActivityCount, bool IsNew,
    DateOnly FirstVisit, DateOnly LastVisit);
public sealed record ExplorationMonth(DateOnly Month, int NewCells);
public sealed record ExplorationResult(
    ExplorationIndexStatus Index, ExplorationSummary? Summary,
    IReadOnlyList<ResolvedOwnerPeriod> Periods, PagedResult<ExplorationCell> Cells,
    IReadOnlyList<ExplorationMonth> Months, DateOnly HistoryMonth);
public sealed record ExplorationMapCell(int Zoom, int X, int Y, int VisitedCells, int NewCells,
    int ActivityCount, double West, double South, double East, double North);
public sealed record ExplorationViewport(ExplorationIndexStatus Index, ExplorationSummary? Summary,
    int RenderZoom, IReadOnlyList<ExplorationMapCell> Cells);
public sealed record ExplorationContribution(Guid ActivityId, Guid OwnerId, string OwnerName, string Title,
    SportKind Sport, DateOnly Date, DateTimeOffset StartTimeUtc);
public sealed record ExplorationCellDetail(ExplorationCell Cell, PagedResult<ExplorationContribution> Activities);

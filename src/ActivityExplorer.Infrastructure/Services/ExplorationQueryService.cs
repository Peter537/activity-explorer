using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed class ExplorationQueryService(IDbContextFactory<ExplorerDbContext> contextFactory) : IExplorationQueryService
{
    private const int CellZoom = ExplorationGrid.Zoom;
    private const int CellAxis = ExplorationGrid.Size;
    private const int CellPageSize = 50;
    private const int ContributionPageSize = 25;
    private const int MaximumMapFeatures = 2048;

    public async Task<ExplorationResult> GetAsync(ExplorationQuery query, CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadAsync(query, null, cancellationToken);
        var cells = snapshot.Cells.Values.Where(cell => cell.ActivityCount > 0)
            .OrderByDescending(cell => cell.ActivityCount).ThenBy(cell => cell.CellId).Select(ToCell).ToArray();
        var page = Page(query.Page, cells.Length, CellPageSize);
        var monthCounts = snapshot.Cells.Values
            .Where(cell => !query.Cumulative || cell.FirstDate <= snapshot.Summary?.Through)
            .GroupBy(cell => Month(cell.FirstDate)).ToDictionary(group => group.Key, group => group.Count());
        var months = Enumerable.Range(0, 12).Select(offset => snapshot.HistoryMonth.AddMonths(offset - 11))
            .Select(month => new ExplorationMonth(month, monthCounts.GetValueOrDefault(month))).ToArray();
        return new(snapshot.Index, snapshot.Summary, snapshot.Periods,
            new(cells.Skip((page - 1) * CellPageSize).Take(CellPageSize).ToArray(), cells.Length, page, CellPageSize),
            months, snapshot.HistoryMonth);
    }

    public async Task<ExplorationViewport> GetViewportAsync(
        ExplorationQuery query, MapQuery viewport, CancellationToken cancellationToken = default)
    {
        ValidateViewport(viewport);
        var snapshot = await ReadAsync(query, null, cancellationToken);
        var zoom = Math.Min(CellZoom, viewport.Zoom + 2);
        ExplorationMapCell[] groups;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shift = CellZoom - zoom;
            groups = snapshot.Cells.Values.Where(cell => cell.ActivityCount > 0)
                .GroupBy(cell => (X: (cell.CellId % CellAxis) >> shift, Y: (cell.CellId / CellAxis) >> shift))
                .Select(group => MapCell(zoom, group.Key.X, group.Key.Y, group.Count(),
                    group.Count(cell => cell.IsNew), group.Max(cell => cell.ActivityCount)))
                .Where(cell => Intersects(cell, viewport)).OrderBy(cell => cell.Y).ThenBy(cell => cell.X).ToArray();
            if (groups.Length <= MaximumMapFeatures || zoom == 0) break;
            zoom--;
        } while (true);
        return new(snapshot.Index, snapshot.Summary, zoom, groups);
    }

    public async Task<ExplorationCellDetail?> GetCellAsync(
        ExplorationQuery query, int cellId, int page = 1, CancellationToken cancellationToken = default)
    {
        if (cellId is < 0 or >= CellAxis * CellAxis)
            throw new ArgumentOutOfRangeException(nameof(cellId), "Choose a valid exploration cell.");
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "Choose a positive activity page.");
        var snapshot = await ReadAsync(query, cellId, cancellationToken);
        if (!snapshot.Cells.TryGetValue(cellId, out var cell) || cell.ActivityCount == 0) return null;
        var contributions = snapshot.Contributions.OrderByDescending(activity => activity.Date)
            .ThenByDescending(activity => activity.StartTimeUtc).ThenBy(activity => activity.ActivityId).ToArray();
        page = Page(page, contributions.Length, ContributionPageSize);
        return new(ToCell(cell), new(contributions.Skip((page - 1) * ContributionPageSize).Take(ContributionPageSize).ToArray(),
            contributions.Length, page, ContributionPageSize));
    }

    private async Task<Snapshot> ReadAsync(ExplorationQuery query, int? selectedCell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Scope.Sport.HasValue && !Enum.IsDefined(query.Scope.Sport.Value))
            throw new ArgumentException("Choose a valid sport.", nameof(query));
        if (query.Page < 1) throw new ArgumentOutOfRangeException(nameof(query), "Choose a positive cell page.");
        if (query.HistoryMonth is { Year: 1, Month: < 12 })
            throw new ArgumentOutOfRangeException(nameof(query), "Choose a history month from December 0001 onward.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        // A deferred snapshot keeps all metadata and memberships consistent without reserving the writer lock.
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var enlisted = await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var periods = await ReportingDateQuery.ResolveAsync(db, query.Scope.OwnerId, query.Dates, query.AsOfUtc, cancellationToken);
        var source = db.Activities.AsNoTracking().Where(activity =>
            (!query.Scope.OwnerId.HasValue || activity.OwnerId == query.Scope.OwnerId) &&
            (!query.Scope.Sport.HasValue || activity.Sport == query.Scope.Sport));
        var rows = await (from activity in source
                          join index in db.ActivityExplorationIndexes.AsNoTracking() on activity.Id equals index.ActivityId into indexes
                          from index in indexes.DefaultIfEmpty()
                          select new
                          {
                              activity.Id,
                              activity.OwnerId,
                              activity.StartTimeUtc,
                              activity.Title,
                              activity.Sport,
                              Current = index != null && index.InputVersion == activity.ExplorationInputVersion &&
                                  index.ComputationVersion == ExplorationIndexService.ComputationVersion,
                              Limited = index != null && index.IsLimited,
                              CellCount = index == null ? 0 : index.CellCount
                          }).ToArrayAsync(cancellationToken);
        var status = new ExplorationIndexStatus(rows.Length, rows.Count(row => row.Current), rows.Count(row => row.Current && row.Limited));
        var provisionalHistoryMonth = HistoryMonth(query.HistoryMonth ?? query.Through ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
        if (!status.IsComplete)
            return new(status, null, periods, [], [], provisionalHistoryMonth);

        var periodByOwner = periods.ToDictionary(period => period.OwnerId);
        var zones = periods.ToDictionary(period => period.OwnerId, period => ReportingTimeZone.Resolve(period.TimeZoneId));
        var activities = rows.ToDictionary(row => row.Id, row => new ActivityRow(row.Id, row.OwnerId,
            row.StartTimeUtc, row.Title, row.Sport, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.StartTimeUtc, zones[row.OwnerId]).DateTime),
            row.CellCount));
        var gpsDates = activities.Values.Where(activity => activity.CellCount > 0).Select(activity => activity.Date).ToArray();
        DateOnly? firstDate = gpsDates.Length == 0 ? null : gpsDates.Min();
        DateOnly? lastDate = gpsDates.Length == 0 ? null : gpsDates.Max();
        DateOnly? through = null;
        if (query.Cumulative && firstDate.HasValue && lastDate.HasValue)
        {
            var rangeEnds = periods.Select(period => period.To).Distinct().ToArray();
            var defaultThrough = rangeEnds.Length == 1 && rangeEnds[0].HasValue ? rangeEnds[0]!.Value : lastDate.Value;
            through = query.Through ?? (defaultThrough < firstDate ? firstDate : defaultThrough > lastDate ? lastDate : defaultThrough);
            if (through < firstDate || through > lastDate)
                throw new ArgumentException("Choose a cumulative date within the recorded GPS history.", nameof(query));
        }
        var matching = activities.Values.Where(activity => Matches(activity, periodByOwner[activity.OwnerId], query.Cumulative, through))
            .Select(activity => activity.Id).ToHashSet();
        var cells = new Dictionary<int, CellRow>();
        var contributions = new List<ExplorationContribution>();
        var memberships = from membership in db.ActivityExplorationCells.AsNoTracking()
                          join activity in source on membership.ActivityId equals activity.Id
                          where !selectedCell.HasValue || membership.CellId == selectedCell.Value
                          select new { membership.ActivityId, membership.CellId };
        await foreach (var membership in memberships.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var activity = activities[membership.ActivityId];
            if (!cells.TryGetValue(membership.CellId, out var cell))
                cells.Add(membership.CellId, cell = new CellRow(membership.CellId, activity.Date));
            if (activity.Date < cell.FirstDate) cell.FirstDate = activity.Date;
            if (activity.Date > cell.LastDate) cell.LastDate = activity.Date;
            if (periodByOwner[activity.OwnerId].From is { } from && activity.Date < from)
                cell.VisitedBeforeRange = true;
            if (!matching.Contains(activity.Id)) continue;
            cell.ActivityCount++;
            if (selectedCell.HasValue)
                contributions.Add(new(activity.Id, activity.OwnerId, periodByOwner[activity.OwnerId].OwnerName,
                    activity.Title, activity.Sport, activity.Date, activity.StartTimeUtc));
        }
        foreach (var cell in cells.Values)
            cell.IsNew = query.Cumulative ? cell.FirstDate == through : !cell.VisitedBeforeRange;
        var visited = cells.Values.Where(cell => cell.ActivityCount > 0).ToArray();
        var summary = new ExplorationSummary(visited.Length, visited.Count(cell => cell.IsNew),
            activities.Values.Count(activity => activity.CellCount > 0 && matching.Contains(activity.Id)),
            visited.Length == 0 ? 0 : visited.Max(cell => cell.ActivityCount), firstDate, lastDate, through, status.LimitedActivities);
        var historyMonth = HistoryMonth(query.HistoryMonth ?? through ?? lastDate ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
        await transaction.CommitAsync(cancellationToken);
        return new(status, summary, periods, cells, contributions, historyMonth);
    }

    private static bool Matches(ActivityRow activity, ResolvedOwnerPeriod period, bool cumulative, DateOnly? through) =>
        cumulative ? through.HasValue && activity.Date <= through :
            (!period.FromUtc.HasValue || activity.StartTimeUtc >= period.FromUtc) &&
            (!period.ToUtc.HasValue || activity.StartTimeUtc < period.ToUtc);

    private static DateOnly Month(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateOnly HistoryMonth(DateOnly date) => date.Year == 1 && date.Month < 12 ? new(1, 12, 1) : Month(date);

    private static int Page(int requested, int total, int pageSize) =>
        Math.Min(requested, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));

    private static ExplorationCell ToCell(CellRow cell) => new(cell.CellId, cell.CellId % CellAxis, cell.CellId / CellAxis,
        cell.ActivityCount, cell.IsNew, cell.FirstDate, cell.LastDate);

    private static ExplorationMapCell MapCell(int zoom, int x, int y, int visited, int discovered, int frequency)
    {
        var bounds = ExplorationGrid.Bounds(zoom, x, y);
        return new(zoom, x, y, visited, discovered, frequency, bounds.West, bounds.South, bounds.East, bounds.North);
    }

    private static bool Intersects(ExplorationMapCell cell, MapQuery viewport)
    {
        if (!viewport.West.HasValue) return true;
        if (cell.North < viewport.South || cell.South > viewport.North) return false;
        return viewport.West <= viewport.East
            ? cell.East >= viewport.West && cell.West <= viewport.East
            : cell.East >= viewport.West || cell.West <= viewport.East;
    }

    private static void ValidateViewport(MapQuery viewport)
    {
        var bounds = new[] { viewport.West, viewport.South, viewport.East, viewport.North };
        if (bounds.Count(value => value.HasValue) is not 0 and not 4)
            throw new ArgumentException("Map bounds must include west, south, east, and north.", nameof(viewport));
        if (bounds.Any(value => value.HasValue && !double.IsFinite(value.Value)))
            throw new ArgumentException("Map bounds must be finite numbers.", nameof(viewport));
        if (viewport.West is < -180 or > 180 || viewport.East is < -180 or > 180 ||
            viewport.South is < -90 or > 90 || viewport.North is < -90 or > 90 || viewport.South > viewport.North)
            throw new ArgumentException("Map bounds contain an invalid latitude or longitude range.", nameof(viewport));
        if (viewport.Zoom is < 0 or > 24)
            throw new ArgumentOutOfRangeException(nameof(viewport), "Map zoom must be between 0 and 24.");
    }

    private sealed record Snapshot(ExplorationIndexStatus Index, ExplorationSummary? Summary,
        IReadOnlyList<ResolvedOwnerPeriod> Periods, Dictionary<int, CellRow> Cells,
        IReadOnlyList<ExplorationContribution> Contributions, DateOnly HistoryMonth);

    private sealed record ActivityRow(Guid Id, Guid OwnerId, DateTimeOffset StartTimeUtc, string Title, SportKind Sport,
        DateOnly Date, int CellCount);

    private sealed class CellRow(int cellId, DateOnly date)
    {
        public int CellId { get; } = cellId;
        public int ActivityCount { get; set; }
        public DateOnly FirstDate { get; set; } = date;
        public DateOnly LastDate { get; set; } = date;
        public bool VisitedBeforeRange { get; set; }
        public bool IsNew { get; set; }
    }
}

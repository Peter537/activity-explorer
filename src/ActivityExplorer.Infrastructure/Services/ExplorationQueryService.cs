using System.Globalization;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        var cells = snapshot.Cells.Values.Where(cell => cell.ActivityCount > 0 && (!query.NewOnly || cell.IsNew))
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
            groups = snapshot.Cells.Values.Where(cell => cell.ActivityCount > 0 && (!query.NewOnly || cell.IsNew))
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
        if (!snapshot.Cells.TryGetValue(cellId, out var cell) || cell.ActivityCount == 0 || query.NewOnly && !cell.IsNew) return null;
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
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var mapping = !query.Cumulative && !selectedCell.HasValue
            ? await SqliteReadMapping.BeginAsync(connection, cancellationToken) : null;
        await using var transaction = await ReadSnapshot.BeginOnOpenConnectionAsync(db, cancellationToken);
        var periods = await ReportingDateQuery.ResolveAsync(db, query.Scope.OwnerId, query.Dates, query.AsOfUtc, cancellationToken);
        var source = db.Activities.AsNoTracking().Where(activity =>
            (!query.Scope.OwnerId.HasValue || activity.OwnerId == query.Scope.OwnerId) &&
            (!query.Scope.Sport.HasValue || activity.Sport == query.Scope.Sport));
        if (!selectedCell.HasValue && !query.Cumulative)
        {
            var hasAggregate = await db.Database.SqlQueryRaw<long>("""
                SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'ActivityExplorationAggregateRows') AS "Value"
                """).SingleAsync(cancellationToken) != 0;
            if (hasAggregate)
            {
                var clustered = await ReadClusteredAggregateAsync();
                if (clustered.Handled)
                {
                    if (clustered.Value!.Index.IsComplete) await transaction.CommitAsync(cancellationToken);
                    return clustered.Value!;
                }
            }
        }

        async Task<(bool Handled, Snapshot? Value)> ReadClusteredAggregateAsync()
        {
            // Compare to the authoritative activity scope in the same snapshot. Missing or
            // invalid derived rows must never make an incomplete index appear complete.
            var expected = await source.CountAsync(cancellationToken);
            // Read primitive fields directly: this avoids an EF projection object per activity,
            // and scoped reads do not materialize the same owner identifier for every row.
            var scopedOwner = query.Scope.OwnerId.HasValue;
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = (scopedOwner ? "SELECT " : "SELECT \"OwnerId\", ") + """
                "StartTimeTicks", "ExplorationInputVersion", "InputVersion", "ComputationVersion",
                "IsLimited", "CellCount", "CellsJson", "FormatVersion", "AggregateVersion"
                FROM "ActivityExplorationAggregateRows" WHERE 1 = 1
                """;
            if (scopedOwner)
            {
                // Matches Microsoft.Data.Sqlite's uppercase D-format Guid binding. Reading
                // unscoped owner values as text permits malformed derived data to fall back.
                command.CommandText += " AND \"OwnerId\" = @owner";
                command.Parameters.AddWithValue("@owner", query.Scope.OwnerId!.Value.ToString("D").ToUpperInvariant());
            }
            if (query.Scope.Sport.HasValue)
            {
                command.CommandText += " AND \"Sport\" = @sport";
                command.Parameters.AddWithValue("@sport", (int)query.Scope.Sport.Value);
            }
            var clusterPeriods = periods.ToDictionary(period => period.OwnerId);
            var clusterZones = periods.ToDictionary(period => period.OwnerId, period => ReportingTimeZone.Resolve(period.TimeZoneId));
            var clusterCells = new Dictionary<int, CellRow>();
            var buffer = new List<int>();
            var total = 0;
            var indexed = 0;
            var limited = 0;
            var matchingGps = 0;
            var valid = true;
            string? previousOwner = null;
            ResolvedOwnerPeriod? previousPeriod = null;
            TimeZoneInfo? previousZone = null;
            if (query.Scope.OwnerId.HasValue)
            {
                clusterPeriods.TryGetValue(query.Scope.OwnerId.Value, out previousPeriod);
                clusterZones.TryGetValue(query.Scope.OwnerId.Value, out previousZone);
            }
            DateOnly? first = null;
            DateOnly? last = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total++;
                    var offset = scopedOwner ? 0 : 1;
                    var startTicks = reader.GetInt64(offset);
                    var current = reader.GetInt64(offset + 1) == reader.GetInt64(offset + 2) &&
                        reader.GetInt32(offset + 3) == ExplorationIndexService.ComputationVersion;
                    if (current) { indexed++; if (reader.GetBoolean(offset + 4)) limited++; }
                    if (!valid) continue;
                    var cellCount = reader.GetInt32(offset + 5);
                    var json = reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6);
                    int? format = reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7);
                    if (reader.GetInt32(offset + 8) != ExplorationAggregateStore.FormatVersion ||
                        !TryReadCanonicalCells(json, format, cellCount, buffer) ||
                        startTicks < DateTimeOffset.MinValue.Ticks || startTicks > DateTimeOffset.MaxValue.Ticks)
                    {
                        valid = false;
                        continue;
                    }
                    if (!scopedOwner)
                    {
                        var ownerText = reader.IsDBNull(0) ? null : reader.GetString(0);
                        if (!string.Equals(previousOwner, ownerText, StringComparison.Ordinal))
                        {
                            if (!Guid.TryParse(ownerText, out var ownerId) ||
                                !clusterPeriods.TryGetValue(ownerId, out previousPeriod) ||
                                !clusterZones.TryGetValue(ownerId, out previousZone))
                            {
                                valid = false;
                                continue;
                            }
                            previousOwner = ownerText;
                        }
                    }
                    if (previousPeriod is null || previousZone is null)
                    {
                        valid = false;
                        continue;
                    }
                    var period = previousPeriod;
                    var start = new DateTimeOffset(startTicks, TimeSpan.Zero);
                    var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, previousZone).DateTime);
                    var matches = (!period.FromUtc.HasValue || start >= period.FromUtc) &&
                        (!period.ToUtc.HasValue || start < period.ToUtc);
                    if (cellCount > 0)
                    {
                        if (!first.HasValue || date < first) first = date;
                        if (!last.HasValue || date > last) last = date;
                        if (matches) matchingGps++;
                    }
                    foreach (var cellId in buffer)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AccumulateCell(clusterCells, cellId, date, period, matches);
                    }
                }
            }
            if (!valid || total != expected) return (false, null);
            var clusterStatus = new ExplorationIndexStatus(total, indexed, limited);
            var provisional = HistoryMonth(query.HistoryMonth ?? query.Through ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
            if (!clusterStatus.IsComplete) return (true, new(clusterStatus, null, periods, [], [], provisional));
            foreach (var cell in clusterCells.Values) cell.IsNew = !cell.VisitedBeforeRange;
            var clusterVisited = clusterCells.Values.Where(cell => cell.ActivityCount > 0).ToArray();
            var clusterSummary = new ExplorationSummary(clusterVisited.Length, clusterVisited.Count(cell => cell.IsNew), matchingGps,
                clusterVisited.Length == 0 ? 0 : clusterVisited.Max(cell => cell.ActivityCount), first, last, null, clusterStatus.LimitedActivities);
            var month = HistoryMonth(query.HistoryMonth ?? last ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
            return (true, new(clusterStatus, clusterSummary, periods, clusterCells, [], month));
        }

        // SQLite favors the unique primary-key index even when a covering index avoids table reads.
        // Read-only databases created before the additive upgrade retain the ordinary join.
        var hasReadCover = await db.Database.SqlQueryRaw<long>("""
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'index' AND name = 'IX_ActivityExplorationIndexes_ReadCover') AS "Value"
            """).SingleAsync(cancellationToken) != 0;
        IQueryable<ActivityExplorationIndex> explorationIndexes = hasReadCover
            ? db.ActivityExplorationIndexes.FromSqlRaw("""
                SELECT * FROM "ActivityExplorationIndexes" INDEXED BY "IX_ActivityExplorationIndexes_ReadCover"
                """)
            : db.ActivityExplorationIndexes;
        var metadata = from activity in source
                       join index in explorationIndexes.AsNoTracking() on activity.Id equals index.ActivityId into indexes
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
                       };
        var hasCellSets = !selectedCell.HasValue && await db.Database.SqlQueryRaw<long>("""
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'ActivityExplorationCellSets') AS "Value"
            """).SingleAsync(cancellationToken) != 0;
        ExplorationMetadata[] rows;
        if (hasCellSets)
        {
            var sets = db.Database.SqlQueryRaw<CellSetProjection>("""
                SELECT "ActivityId", "CellsJson", "FormatVersion" FROM "ActivityExplorationCellSets"
                """);
            rows = await (from activity in metadata
                          join set in sets on activity.Id equals set.ActivityId into cellSets
                          from set in cellSets.DefaultIfEmpty()
                          orderby activity.OwnerId, activity.Id
                          select new ExplorationMetadata(activity.Id, activity.OwnerId, activity.StartTimeUtc,
                              activity.Title, activity.Sport, activity.Current, activity.Limited, activity.CellCount,
                              set.CellsJson, set.FormatVersion)).ToArrayAsync(cancellationToken);
        }
        else
        {
            rows = await metadata.OrderBy(activity => activity.OwnerId).ThenBy(activity => activity.Id)
                .Select(activity => new ExplorationMetadata(activity.Id, activity.OwnerId, activity.StartTimeUtc,
                    activity.Title, activity.Sport, activity.Current, activity.Limited, activity.CellCount, null, null))
                .ToArrayAsync(cancellationToken);
        }
        var status = new ExplorationIndexStatus(rows.Length, rows.Count(row => row.Current), rows.Count(row => row.Current && row.Limited));
        var provisionalHistoryMonth = HistoryMonth(query.HistoryMonth ?? query.Through ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
        if (!status.IsComplete)
            return new(status, null, periods, [], [], provisionalHistoryMonth);

        var periodByOwner = periods.ToDictionary(period => period.OwnerId);
        var zones = periods.ToDictionary(period => period.OwnerId, period => ReportingTimeZone.Resolve(period.TimeZoneId));
        var useCellSets = hasCellSets;
        if (useCellSets)
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadCells(row.CellsJson, row.FormatVersion, row.CellCount, out var cellIds))
                {
                    useCellSets = false;
                    break;
                }
                row.CellIds = cellIds;
            }
        }
        var activities = rows.ToDictionary(row => row.Id, row => new ActivityRow(row.Id, row.OwnerId,
            row.StartTimeUtc, row.Title, row.Sport, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(row.StartTimeUtc, zones[row.OwnerId]).DateTime),
            row.CellCount, row.CellIds));
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
        foreach (var activity in activities.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity.IsMatching = Matches(activity, periodByOwner[activity.OwnerId], query.Cumulative, through);
        }
        var cells = new Dictionary<int, CellRow>();
        var contributions = new List<ExplorationContribution>();
        if (useCellSets)
        {
            foreach (var activity in activities.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var cellId in activity.CellIds!)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddMembership(activity, cellId);
                }
            }
        }
        else
        {
            var dense = false;
            if (!selectedCell.HasValue)
            {
                // Header counts choose the access path only; SQL membership remains authoritative.
                var scopedCells = rows.Sum(row => (long)Math.Max(0, row.CellCount));
                if (scopedCells >= 1024)
                {
                    var totalCells = await db.ActivityExplorationCells.LongCountAsync(cancellationToken);
                    dense = totalCells > 0 && scopedCells >= totalCells / 3 + (totalCells % 3 == 0 ? 0 : 1);
                }
            }
            var memberships = dense
                ? db.ActivityExplorationCells.FromSqlRaw("SELECT * FROM \"ActivityExplorationCells\" NOT INDEXED")
                    .AsNoTracking().Where(membership => source.Select(activity => activity.Id).Contains(membership.ActivityId))
                    .Select(membership => new { membership.ActivityId, membership.CellId })
                : from membership in db.ActivityExplorationCells.AsNoTracking()
                  join activity in source on membership.ActivityId equals activity.Id
                  where !selectedCell.HasValue || membership.CellId == selectedCell.Value
                  select new { membership.ActivityId, membership.CellId };
            await foreach (var membership in memberships.AsAsyncEnumerable().WithCancellation(cancellationToken))
                AddMembership(activities[membership.ActivityId], membership.CellId);
        }

        void AddMembership(ActivityRow activity, int cellId)
        {
            AccumulateCell(cells, cellId, activity.Date, periodByOwner[activity.OwnerId], activity.IsMatching);
            if (activity.IsMatching && selectedCell.HasValue)
                contributions.Add(new(activity.Id, activity.OwnerId, periodByOwner[activity.OwnerId].OwnerName,
                    activity.Title, activity.Sport, activity.Date, activity.StartTimeUtc));
        }
        foreach (var cell in cells.Values)
            cell.IsNew = query.Cumulative ? cell.FirstDate == through : !cell.VisitedBeforeRange;
        var visited = cells.Values.Where(cell => cell.ActivityCount > 0).ToArray();
        var summary = new ExplorationSummary(visited.Length, visited.Count(cell => cell.IsNew),
            activities.Values.Count(activity => activity.CellCount > 0 && activity.IsMatching),
            visited.Length == 0 ? 0 : visited.Max(cell => cell.ActivityCount), firstDate, lastDate, through, status.LimitedActivities);
        var historyMonth = HistoryMonth(query.HistoryMonth ?? through ?? lastDate ?? DateOnly.FromDateTime(query.AsOfUtc.UtcDateTime));
        await transaction.CommitAsync(cancellationToken);
        return new(status, summary, periods, cells, contributions, historyMonth);
    }

    private static void AccumulateCell(Dictionary<int, CellRow> cells, int cellId, DateOnly date,
        ResolvedOwnerPeriod period, bool matches)
    {
        if (!cells.TryGetValue(cellId, out var cell)) cells.Add(cellId, cell = new CellRow(cellId, date));
        if (date < cell.FirstDate) cell.FirstDate = date;
        if (date > cell.LastDate) cell.LastDate = date;
        if (period.From is { } from && date < from) cell.VisitedBeforeRange = true;
        if (matches) cell.ActivityCount++;
    }

    // Stored sets are canonical arrays of sorted integers. Other valid JSON safely uses
    // the authoritative fallback rather than expanding this hot-path parser's contract.
    private static bool TryReadCanonicalCells(string? json, int? version, int expectedCount, List<int> cells)
    {
        cells.Clear();
        if (version != ExplorationCellSetStore.FormatVersion || json is null || json.Length < 2 ||
            json[0] != '[' || json[^1] != ']') return false;
        var span = json.AsSpan();
        var position = 1;
        if (position == span.Length - 1) return expectedCount == 0;
        while (position < span.Length - 1)
        {
            var start = position;
            if (span[position] == '-') position++;
            if (position >= span.Length - 1 || span[position] is < '0' or > '9') return false;
            var firstDigit = position;
            while (position < span.Length - 1 && span[position] is >= '0' and <= '9') position++;
            if (span[firstDigit] == '0' && position - firstDigit > 1) return false;
            if (!int.TryParse(span[start..position], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var cell) ||
                cells.Count > 0 && cell <= cells[^1]) return false;
            cells.Add(cell);
            if (position == span.Length - 1) break;
            if (span[position++] != ',' || position == span.Length - 1) return false;
        }
        return cells.Count == expectedCount;
    }

    private static bool TryReadCells(string? json, int? version, int expectedCount, out int[]? cells)
    {
        cells = null;
        if (version != ExplorationCellSetStore.FormatVersion || json is null) return false;
        try { cells = JsonSerializer.Deserialize<int[]>(json); }
        catch (JsonException) { return false; }
        if (cells is null || cells.Length != expectedCount) return false;
        for (var index = 1; index < cells.Length; index++)
            if (cells[index] <= cells[index - 1]) return false;
        return true;
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

    private sealed class CellSetProjection
    {
        public Guid ActivityId { get; set; }
        public string? CellsJson { get; set; }
        public int? FormatVersion { get; set; }
    }

    private sealed record ExplorationMetadata(Guid Id, Guid OwnerId, DateTimeOffset StartTimeUtc, string Title,
        SportKind Sport, bool Current, bool Limited, int CellCount, string? CellsJson, int? FormatVersion)
    {
        public int[]? CellIds { get; set; }
    }

    private sealed record ActivityRow(Guid Id, Guid OwnerId, DateTimeOffset StartTimeUtc, string Title, SportKind Sport,
        DateOnly Date, int CellCount, int[]? CellIds)
    {
        public bool IsMatching { get; set; }
    }

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

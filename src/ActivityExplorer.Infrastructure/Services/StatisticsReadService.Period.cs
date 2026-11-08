using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Services;

public sealed partial class StatisticsReadService
{
    private sealed record OverviewActivity(Guid Id, Guid OwnerId, string Title, SportKind Sport,
        DateTimeOffset StartTimeUtc, double DistanceMeters, double MovingTimeSeconds, double ElevationGainMeters);
    private readonly record struct OverviewCandidate(OverviewActivity Activity, double Value, double CoveragePercent,
        double? StartPosition = null, double? FinishPosition = null);
    private sealed record OverviewPayload(Guid ActivityId, byte[] CompressedPayload);
    private sealed record OverviewRowAddress(Guid ActivityId, long RowId);

    public async Task<RecordOverviewResult> GetOverviewAsync(RecordOverviewQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(query.Scope)) throw new ArgumentException("Choose a valid record scope.", nameof(query));
        if (query.Sport.HasValue && !Enum.IsDefined(query.Sport.Value))
            throw new ArgumentException("Choose a valid record sport.", nameof(query));
        var selection = query.Period ?? new(ReportingPreset.AllTime);
        ReportingDates.Validate(selection);
        var asOf = query.AsOfUtc ?? (timeProvider ?? TimeProvider.System).GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await using var mapping = selection.Preset != ReportingPreset.AllTime && query.OwnerId.HasValue && query.Sport.HasValue
            ? await Storage.SqliteReadMapping.BeginAsync(connection, cancellationToken) : null;
        await using var transaction = await Storage.ReadSnapshot.BeginOnOpenConnectionAsync(db, cancellationToken);
        if (selection.Preset == ReportingPreset.AllTime)
        {
            var periods = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection, asOf, cancellationToken);
            var records = await ReadRecordsAsync(db, query.OwnerId, query.Scope, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new(records.Where(record => !query.Sport.HasValue || record.Sport == query.Sport).ToArray(), periods);
        }

        var resolved = await ReportingDateQuery.ResolveAsync(db, query.OwnerId, selection, asOf, cancellationToken);
        var ownerNames = resolved.ToDictionary(period => period.OwnerId, period => period.OwnerName);
        var source = ReportingDateQuery.Apply(db.Activities.AsNoTracking(), resolved);
        if (query.Sport.HasValue) source = source.Where(activity => activity.Sport == query.Sport);
        if (query.Scope != RecordScope.All) source = source.Where(activity => activity.IsIndoor == (query.Scope == RecordScope.Indoor));
        var benchmarks = Enum.GetValues<SportKind>().ToDictionary(sport => sport, OverviewWholeBenchmarks);
        var effortGroups = benchmarks.Keys.ToDictionary(sport => sport, Storage.RecordEffortReadModel.GroupsFor);
        var winners = new Dictionary<(Guid Owner, SportKind Sport, RecordKind Kind, string Key), (RecordBenchmark Benchmark, OverviewCandidate Candidate)>();
        var rows = source.Select(activity => new OverviewActivity(activity.Id, activity.OwnerId,
                activity.Title, activity.Sport, activity.StartTimeUtc,
                activity.DistanceMeters, activity.MovingTimeSeconds, activity.ElevationGainMeters));
        await Task.Run(async () =>
        {
            if (query.OwnerId.HasValue && query.Sport is { } sport && resolved.Count == 1 &&
                await Storage.PeriodRecordProjectionStore.ExistsAsync(db, cancellationToken) &&
                await TryReadProjectionAsync(resolved[0], sport)) return;

            // Keep scalar eligibility separate from payload reads, including activities without streams.
            var eligible = new Dictionary<Guid, OverviewActivity>();
            await foreach (var activity in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                eligible.Add(activity.Id, activity);
                ConsiderWholeActivity(activity);
            }
            if (eligible.Count == 0) return;
            var selectedIds = source.Select(activity => activity.Id);
            var available = new HashSet<Guid>();
            if (await Storage.RecordEffortReadModel.ExistsAsync(db, cancellationToken))
            {
                IQueryable<Storage.ActivityRecordEffortSummary> summaries = db.ActivityRecordEffortSummaries;
                if (eligible.Count >= 1024)
                {
                    var rowIds = await ReadRowIdsAsync(db.Database.SqlQueryRaw<OverviewRowAddress>(
                        "SELECT \"ActivityId\", rowid AS \"RowId\" FROM \"ActivityRecordEffortSummaries\""), selectedIds, cancellationToken);
                    var keys = new SqliteParameter("@effortRowIds", JsonSerializer.Serialize(rowIds));
                    summaries = db.ActivityRecordEffortSummaries.FromSqlRaw(
                        "SELECT * FROM \"ActivityRecordEffortSummaries\" WHERE rowid IN (SELECT value FROM json_each(@effortRowIds)) ORDER BY rowid", keys);
                }
                else summaries = summaries.Where(summary => selectedIds.Contains(summary.ActivityId));
                summaries = summaries.AsNoTracking().Where(summary =>
                    summary.ComputationVersion == Storage.RecordEffortReadModel.ComputationVersion && summary.ValuesBlob != null);
                var values = new (RecordBenchmark Benchmark, EffortValue Value)[Storage.RecordEffortReadModel.MaximumBenchmarkCount];
                await foreach (var summary in summaries.AsAsyncEnumerable().WithCancellation(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var activity = eligible[summary.ActivityId];
                    if (summary.Sport != activity.Sport || !Storage.RecordEffortReadModel.TryRead(summary.ValuesBlob, summary.Sport, values, out var count))
                        continue;
                    ConsiderEfforts(activity, values.AsSpan(0, count));
                    available.Add(activity.Id);
                }
            }
            var pendingCount = eligible.Count - available.Count;
            if (pendingCount == 0) return;
            if (available.Count > 0)
            {
                var keys = new SqliteParameter("@pendingIds", JsonSerializer.Serialize(eligible.Keys.Where(id => !available.Contains(id))));
                var pendingIds = db.Database.SqlQueryRaw<Guid>("SELECT value AS \"Value\" FROM json_each(@pendingIds)", keys);
                // Existing databases can store the same Guid text in either case.
                selectedIds = selectedIds.Where(id => pendingIds.Contains(EF.Functions.Collate(id, "NOCASE")));
            }
            IQueryable<OverviewPayload> payloads;
            if (pendingCount >= 1024)
            {
                // Sort only physical keys before reading BLOBs; all keys stay inside this read snapshot.
                var physicalKeys = await ReadRowIdsAsync(db.Database.SqlQueryRaw<OverviewRowAddress>(
                    "SELECT \"ActivityId\", rowid AS \"RowId\" FROM \"ActivityStreams\""), selectedIds, cancellationToken);
                if (physicalKeys.Length == 0) return;
                cancellationToken.ThrowIfCancellationRequested();
                var keys = new SqliteParameter("@physicalKeys", JsonSerializer.Serialize(physicalKeys));
                payloads = db.ActivityStreams.FromSqlRaw(
                        "SELECT * FROM \"ActivityStreams\" WHERE rowid IN (SELECT value FROM json_each(@physicalKeys)) ORDER BY rowid", keys)
                    .AsNoTracking().Select(stream => new OverviewPayload(stream.ActivityId, stream.CompressedPayload));
            }
            else
                payloads = db.ActivityStreams.Where(stream => selectedIds.Contains(stream.ActivityId)).AsNoTracking()
                    .Select(stream => new OverviewPayload(stream.ActivityId, stream.CompressedPayload));
            // One serialized reader feeds four-row batches to at most four workers.
            await Parallel.ForEachAsync(ReadOverviewBatches(payloads, eligible, cancellationToken),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = cancellationToken },
            async (batch, workerToken) =>
            {
                var candidates = new List<(RecordBenchmark Benchmark, OverviewCandidate Candidate)>();
                foreach (var (activity, payload) in batch)
                {
                    workerToken.ThrowIfCancellationRequested();
                    if (payload is not null)
                    {
                        var points = await TrackCodec.DecodeAsync(payload, workerToken);
                        foreach (var (benchmark, value) in BestEffortCalculator.BestValues(
                                     points, activity.Sport, effortGroups[activity.Sport], workerToken))
                            candidates.Add((benchmark, new(activity, value.Value, value.CoveragePercent, value.StartPosition, value.FinishPosition)));
                    }
                }
                workerToken.ThrowIfCancellationRequested();
                lock (winners)
                {
                    workerToken.ThrowIfCancellationRequested();
                    foreach (var candidate in candidates) Consider(candidate.Benchmark, candidate.Candidate);
                }
            });
        }, cancellationToken);

        async Task<bool> TryReadProjectionAsync(ResolvedOwnerPeriod period, SportKind sport)
        {
            var expected = await source.CountAsync(cancellationToken);
            if (expected < 1024) return false;

            var wholeBenchmarks = benchmarks[sport];
            var slotBenchmarks = wholeBenchmarks.Concat(effortGroups[sport].SelectMany(group => group)).ToArray();
            var effortSlots = new Dictionary<RecordBenchmark, int>(ReferenceEqualityComparer.Instance);
            for (var slot = wholeBenchmarks.Length; slot < slotBenchmarks.Length; slot++)
                effortSlots.Add(slotBenchmarks[slot], slot);
            var projectedWinners = new OverviewCandidate?[slotBenchmarks.Length];
            var seen = 0;
            var values = new (RecordBenchmark Benchmark, EffortValue Value)[Storage.RecordEffortReadModel.MaximumBenchmarkCount];
            await foreach (var row in Storage.PeriodRecordProjectionStore.Read(db, period, sport, query.Scope)
                               .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = 0;
                if (row.HasStream && (row.SummarySport != row.Sport ||
                    row.ComputationVersion != Storage.RecordEffortReadModel.ComputationVersion ||
                    !Storage.RecordEffortReadModel.TryRead(row.ValuesBlob, row.Sport, values, out count))) return false;

                OverviewActivity? activity = null;
                for (var slot = 0; slot < wholeBenchmarks.Length; slot++)
                {
                    var value = WholeActivityValue(wholeBenchmarks[slot].Kind, row.DistanceMeters,
                        row.MovingTimeSeconds, row.ElevationGainMeters);
                    if (double.IsFinite(value) && value > 0) ConsiderSlot(row, ref activity, slot, value, 100);
                }
                for (var index = 0; index < count; index++)
                {
                    var (benchmark, value) = values[index];
                    ConsiderSlot(row, ref activity, effortSlots[benchmark], value.Value, value.CoveragePercent,
                        value.StartPosition, value.FinishPosition);
                }
                seen++;
            }

            if (seen != expected) return false;
            for (var slot = 0; slot < projectedWinners.Length; slot++)
                if (projectedWinners[slot] is { } winner) Consider(slotBenchmarks[slot], winner);
            return true;

            void ConsiderSlot(Storage.PeriodRecordProjectionStore.Row row, ref OverviewActivity? activity, int slot,
                double value, double coverage, double? start = null, double? finish = null)
            {
                ref var current = ref projectedWinners[slot];
                if (current is { } winner && CompareOverviewCandidates(value, row.StartTicks, row.ActivityId,
                        start, finish, winner, slotBenchmarks[slot].LowerIsBetter) >= 0) return;

                activity ??= new(row.ActivityId, row.OwnerId, row.Title, row.Sport, new(row.StartTicks, TimeSpan.Zero),
                    row.DistanceMeters, row.MovingTimeSeconds, row.ElevationGainMeters);
                current = new(activity, value, coverage, start, finish);
            }
        }

        void ConsiderWholeActivity(OverviewActivity activity)
        {
            foreach (var benchmark in benchmarks[activity.Sport])
            {
                var value = WholeActivityValue(benchmark.Kind, activity.DistanceMeters, activity.MovingTimeSeconds,
                    activity.ElevationGainMeters);
                if (double.IsFinite(value) && value > 0)
                    Consider(benchmark, new(activity, value, 100));
            }
        }

        void ConsiderEfforts(OverviewActivity activity, ReadOnlySpan<(RecordBenchmark Benchmark, EffortValue Value)> values)
        {
            foreach (var (benchmark, value) in values)
                Consider(benchmark, new(activity, value.Value, value.CoveragePercent, value.StartPosition, value.FinishPosition));
        }

        void Consider(RecordBenchmark benchmark, OverviewCandidate candidate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (candidate.Activity.OwnerId, benchmark.Sport, benchmark.Kind, benchmark.Key);
            if (!winners.TryGetValue(key, out var current) ||
                CompareOverviewCandidates(candidate, current.Candidate, benchmark.LowerIsBetter) < 0)
                winners[key] = (benchmark, candidate);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        var output = winners.Values.Select(winner =>
            {
                var (benchmark, candidate) = winner;
                var activity = candidate.Activity;
                return new PersonalRecord(OverviewRecordId(activity.OwnerId, benchmark), activity.OwnerId, ownerNames[activity.OwnerId],
                    benchmark.Sport, benchmark.Kind, benchmark.Key, candidate.Value, activity.Id, activity.Title,
                    candidate.CoveragePercent, activity.StartTimeUtc);
            })
            .OrderBy(record => record.Sport).ThenBy(record => RecordCatalog.CategoryOrder(record.Kind))
            .ThenBy(record => RecordCatalog.TargetOrder(record.Sport, record.Kind, record.Key))
            .ThenBy(record => record.Key, StringComparer.Ordinal).ThenBy(record => record.OwnerId).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(output, resolved);
    }

    private static Task<long[]> ReadRowIdsAsync(IQueryable<OverviewRowAddress> rows, IQueryable<Guid> selectedIds,
        CancellationToken cancellationToken) => rows.Where(row => selectedIds.Contains(row.ActivityId))
        .OrderBy(row => row.RowId).Select(row => row.RowId).ToArrayAsync(cancellationToken);

    private static async IAsyncEnumerable<(OverviewActivity Activity, byte[] Payload)[]> ReadOverviewBatches(
        IQueryable<OverviewPayload> rows, IReadOnlyDictionary<Guid, OverviewActivity> eligible,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new (OverviewActivity Activity, byte[] Payload)[4];
        var count = 0;
        await foreach (var stream in rows.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            batch[count++] = (eligible[stream.ActivityId], stream.CompressedPayload);
            if (count != batch.Length) continue;
            yield return batch;
            batch = new (OverviewActivity Activity, byte[] Payload)[4];
            count = 0;
        }
        if (count > 0) yield return batch[..count];
    }

    private static RecordBenchmark[] OverviewWholeBenchmarks(SportKind sport)
    {
        var summary = new[]
        {
            RecordCatalog.Find(sport, RecordKind.Distance, "Longest distance"),
            RecordCatalog.Find(sport, RecordKind.Duration, "Longest moving time"),
            RecordCatalog.Find(sport, RecordKind.Elevation, "Most elevation gain"),
            RecordCatalog.Find(sport, RecordKind.AverageSpeed, sport == SportKind.Rowing
                ? "Best average split (activities >= 1 km)" : "Best average speed (activities >= 1 km)")
        };
        return summary.OfType<RecordBenchmark>().ToArray();
    }

    private static double WholeActivityValue(RecordKind kind, double distance, double duration, double elevation) => kind switch
    {
        RecordKind.Distance => distance,
        RecordKind.Duration => duration,
        RecordKind.Elevation => elevation,
        RecordKind.AverageSpeed when distance >= 1000 && duration > 0 => distance / duration,
        _ => 0
    };

    private static int CompareOverviewCandidates(in OverviewCandidate left, in OverviewCandidate right, bool lowerIsBetter) =>
        CompareOverviewCandidates(left.Value, left.Activity.StartTimeUtc.UtcTicks, left.Activity.Id,
            left.StartPosition, left.FinishPosition, right, lowerIsBetter);

    private static int CompareOverviewCandidates(double value, long startTicks, Guid activityId, double? start, double? finish,
        in OverviewCandidate right, bool lowerIsBetter)
    {
        var comparison = value.CompareTo(right.Value) * (lowerIsBetter ? 1 : -1);
        if (comparison != 0) return comparison;
        comparison = startTicks.CompareTo(right.Activity.StartTimeUtc.UtcTicks);
        if (comparison != 0) return comparison;
        comparison = activityId.CompareTo(right.Activity.Id);
        if (comparison != 0) return comparison;
        comparison = Nullable.Compare(start, right.StartPosition);
        return comparison != 0 ? comparison : Nullable.Compare(finish, right.FinishPosition);
    }

    private static Guid OverviewRecordId(Guid owner, RecordBenchmark benchmark)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{owner:N}|{(int)benchmark.Sport}|{(int)benchmark.Kind}|{benchmark.Key}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    }
}

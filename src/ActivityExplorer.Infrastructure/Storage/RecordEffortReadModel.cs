using System.Buffers.Binary;
using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Infrastructure.Storage;

internal sealed class ActivityRecordEffortSummary
{
    public Guid ActivityId { get; set; }
    public SportKind Sport { get; set; }
    public int ComputationVersion { get; set; }
    public byte[]? ValuesBlob { get; set; }
}

internal static class RecordEffortReadModel
{
    internal const string TableName = "ActivityRecordEffortSummaries";
    private const byte FormatVersion = 2;
    private const int EntrySize = sizeof(ushort) + 4 * sizeof(double);
    internal const int ComputationVersion = RecordCatalog.ComputationVersion * 100 + FormatVersion;
    private const int BackfillBatchSize = 64;
    private static readonly Dictionary<SportKind, EffortCatalog> Catalogs = Enum.GetValues<SportKind>()
        .ToDictionary(sport => sport, MakeCatalog);

    private sealed record EffortCatalog(RecordBenchmark[][] Groups, RecordBenchmark[] Benchmarks);
    private sealed record BackfillRow(long RowId, string ActivityId, int SchemaVersion, byte[] CompressedPayload, SportKind? Sport);

    internal static IReadOnlyList<RecordBenchmark[]> GroupsFor(SportKind sport) => Catalogs[sport].Groups;

    internal static int MaximumBenchmarkCount { get; } = Catalogs.Values.Max(catalog => catalog.Benchmarks.Length);

    internal static async Task<bool> ExistsAsync(ExplorerDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM sqlite_schema WHERE type = 'table' AND name = 'ActivityRecordEffortSummaries'")
            .SingleAsync(cancellationToken) == 1;

    internal static byte[] Build(IReadOnlyList<TrackPoint> points, SportKind sport, CancellationToken cancellationToken)
    {
        var catalog = Catalogs[sport];
        var values = BestEffortCalculator.BestValues(points, sport, catalog.Groups, cancellationToken);
        // A format byte distinguishes an empty winner list from a missing or failed summary.
        var payload = new byte[1 + values.Count * EntrySize];
        payload[0] = FormatVersion;
        for (var index = 0; index < values.Count; index++)
        {
            var (benchmark, value) = values[index];
            var entry = payload.AsSpan(1 + index * EntrySize, EntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, checked((ushort)Array.IndexOf(catalog.Benchmarks, benchmark)));
            var fields = entry[sizeof(ushort)..];
            BinaryPrimitives.WriteDoubleLittleEndian(fields, value.Value);
            BinaryPrimitives.WriteDoubleLittleEndian(fields[sizeof(double)..], value.CoveragePercent);
            BinaryPrimitives.WriteDoubleLittleEndian(fields[(2 * sizeof(double))..], value.StartPosition);
            BinaryPrimitives.WriteDoubleLittleEndian(fields[(3 * sizeof(double))..], value.FinishPosition);
        }
        return payload;
    }

    internal static (RecordBenchmark Benchmark, EffortValue Value)[]? Read(byte[]? payload, SportKind sport)
    {
        var entries = CountEntries(payload);
        if (entries < 0 || entries > MaximumBenchmarkCount) return null;
        var result = new (RecordBenchmark Benchmark, EffortValue Value)[entries];
        return TryRead(payload, sport, result, out _) ? result : null;
    }

    internal static bool TryRead(byte[]? payload, SportKind sport, Span<(RecordBenchmark Benchmark, EffortValue Value)> buffer, out int count)
    {
        count = 0;
        var entries = CountEntries(payload);
        if (entries < 0 || entries > buffer.Length || !Catalogs.TryGetValue(sport, out var catalog) || entries > catalog.Benchmarks.Length)
            return false;
        var previous = -1;
        for (var index = 0; index < entries; index++)
        {
            var entry = payload!.AsSpan(1 + index * EntrySize, EntrySize);
            var target = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (target <= previous || target >= catalog.Benchmarks.Length) return false;
            var fields = entry[sizeof(ushort)..];
            var value = new EffortValue(BinaryPrimitives.ReadDoubleLittleEndian(fields),
                BinaryPrimitives.ReadDoubleLittleEndian(fields[sizeof(double)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(fields[(2 * sizeof(double))..]),
                BinaryPrimitives.ReadDoubleLittleEndian(fields[(3 * sizeof(double))..]));
            if (!double.IsFinite(value.Value) || value.Value <= 0 || !double.IsFinite(value.CoveragePercent) || value.CoveragePercent <= 0 ||
                !double.IsFinite(value.StartPosition) || !double.IsFinite(value.FinishPosition) || value.StartPosition < 0 ||
                value.FinishPosition < value.StartPosition) return false;
            previous = target;
            buffer[index] = (catalog.Benchmarks[target], value);
        }
        count = entries;
        return true;
    }

    private static int CountEntries(byte[]? payload) =>
        payload is { Length: > 0 } && payload[0] == FormatVersion && (payload.Length - 1) % EntrySize == 0
            ? (payload.Length - 1) / EntrySize : -1;

    internal static Task SaveAsync(ExplorerDbContext db, Guid activityId, SportKind sport, byte[] valuesBlob, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ActivityRecordEffortSummaries" ("ActivityId", "Sport", "ComputationVersion", "ValuesBlob")
            VALUES ({activityId}, {(int)sport}, {ComputationVersion}, {valuesBlob})
            ON CONFLICT ("ActivityId") DO UPDATE SET "Sport" = excluded."Sport",
                "ComputationVersion" = excluded."ComputationVersion", "ValuesBlob" = excluded."ValuesBlob"
            """, cancellationToken);

    internal static async Task BackfillAsync(ExplorerDbContext db, CancellationToken cancellationToken)
    {
        var nextRowId = long.MinValue;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await db.Database.SqlQuery<BackfillRow>($"""
                WITH pending AS MATERIALIZED (
                    SELECT s.rowid AS "RowId"
                    FROM "ActivityStreams" AS s
                    LEFT JOIN "ActivityRecordEffortSummaries" AS e ON e."ActivityId" = s."ActivityId"
                    WHERE s.rowid >= {nextRowId} AND (e."ActivityId" IS NULL OR e."ComputationVersion" <> {ComputationVersion})
                    ORDER BY s.rowid LIMIT {BackfillBatchSize}
                )
                SELECT p."RowId", s."ActivityId", s."SchemaVersion", s."CompressedPayload", a."Sport"
                FROM pending AS p
                INNER JOIN "ActivityStreams" AS s ON s.rowid = p."RowId"
                LEFT JOIN "Activities" AS a ON a."Id" = s."ActivityId"
                ORDER BY p."RowId"
                """).ToArrayAsync(cancellationToken);
            if (batch.Length == 0) return;

            byte[]?[] values = new byte[batch.Length][];
            for (var index = 0; index < batch.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = batch[index];
                if (row.Sport is not { } sport || !Enum.IsDefined(sport)) continue;
                IReadOnlyList<TrackPoint> points;
                try { points = await TrackCodec.DecodeAsync(row.CompressedPayload, cancellationToken); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or IOException)
                {
                    // A corrupt stream is still read by the fallback, which preserves its existing error behavior.
                    continue;
                }
                if (!points.Any(point => point is null)) values[index] = Build(points, sport, cancellationToken);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            for (var index = 0; index < batch.Length; index++)
            {
                var row = batch[index];
                if (row.Sport is not { } sport) continue;
                // Recheck the source after calculation so an intervening writer cannot leave stale values.
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "ActivityRecordEffortSummaries" ("ActivityId", "Sport", "ComputationVersion", "ValuesBlob")
                    SELECT {row.ActivityId}, {(int)sport}, {ComputationVersion}, {values[index]}
                    WHERE EXISTS (
                        SELECT 1 FROM "ActivityStreams" AS s INNER JOIN "Activities" AS a ON a."Id" = s."ActivityId"
                        WHERE s."ActivityId" = {row.ActivityId} AND s."SchemaVersion" = {row.SchemaVersion}
                            AND s."CompressedPayload" = {row.CompressedPayload} AND a."Sport" = {(int)sport})
                    ON CONFLICT ("ActivityId") DO UPDATE SET "Sport" = excluded."Sport",
                        "ComputationVersion" = excluded."ComputationVersion", "ValuesBlob" = excluded."ValuesBlob"
                    """, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            if (batch[^1].RowId == long.MaxValue) return;
            nextRowId = batch[^1].RowId + 1;
        }
    }

    private static EffortCatalog MakeCatalog(SportKind sport)
    {
        RecordBenchmark[] Group(IReadOnlyList<RecordTarget> targets, RecordKind kind) =>
            targets.Select(target => RecordCatalog.Find(sport, kind, target.Key)!).ToArray();
        RecordBenchmark[][] groups =
        [
            Group(RecordCatalog.DistanceTargets(sport), RecordKind.DistanceEffort),
            Group(RecordCatalog.TimedDistanceTargets(sport), RecordKind.TimedDistanceEffort),
            Group(RecordCatalog.PowerTargets, RecordKind.PowerCurve)
        ];
        return new(groups, groups.SelectMany(group => group).ToArray());
    }
}

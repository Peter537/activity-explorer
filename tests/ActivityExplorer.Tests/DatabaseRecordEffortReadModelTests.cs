using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Activity = ActivityExplorer.Core.Domain.Activity;

namespace ActivityExplorer.Tests;

public sealed class DatabaseRecordEffortReadModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Backfill_commits_bounded_batches_resumes_and_marks_corrupt_streams_for_live_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var capture = new Capture { CancelSecondBackfillRead = cancellation };
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Legacy records", TimeZoneId = "UTC" };
        await using (var db = factory.CreateDbContext())
        {
            db.Add(owner);
            for (var index = 0; index < 70; index++)
            {
                var activity = Row(owner.Id, index);
                if (index == 0) activity.Stream!.CompressedPayload = [0, 1, 2];
                db.Add(activity);
            }
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ActivityRecordEffortSummaries\"");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseInitializer.EnsureRecordEffortSummariesAsync(db, cancellation.Token));
        }

        capture.CancelSecondBackfillRead = null;
        await using var resumed = factory.CreateDbContext();
        Assert.Equal(64, await resumed.ActivityRecordEffortSummaries.CountAsync());
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(resumed);
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(resumed);
        Assert.Equal(70, await resumed.ActivityRecordEffortSummaries.CountAsync());
        Assert.Equal(70, await resumed.ActivityStreams.CountAsync());
        Assert.Equal(1, await resumed.ActivityRecordEffortSummaries.CountAsync(summary => summary.ValuesBlob == null));
        var service = new StatisticsReadService(factory);
        await Assert.ThrowsAnyAsync<Exception>(() => service.GetOverviewAsync(Query(owner.Id)));
    }

    [Fact]
    public async Task Persisted_results_match_live_winners_and_mixed_fallback_handles_legacy_keys_and_stale_rows()
    {
        var capture = new Capture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Mixed records", TimeZoneId = "UTC" };
        var rows = Enumerable.Range(0, 5).Select(index => Row(owner.Id, index)).ToArray();
        rows[4].Id = Guid.Parse("abcdef12-abcd-abcd-abcd-abcdef123456");
        await using var db = factory.CreateDbContext();
        db.Add(owner);
        db.AddRange(rows);
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Activities\" SET \"Id\" = lower(\"Id\") WHERE \"Id\" = {rows[4].Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivityStreams\" SET \"ActivityId\" = lower(\"ActivityId\") WHERE \"ActivityId\" = {rows[4].Id}");
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var service = new StatisticsReadService(factory);
        var live = await service.GetOverviewAsync(Query(owner.Id));
        Assert.Equal(rows[4].Id, Assert.Single(live.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").ActivityId);
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
        Assert.Equal(5, await db.ActivityRecordEffortSummaries.CountAsync());
        capture.Reads.Clear();
        Assert.Equal(live.Records, (await service.GetOverviewAsync(Query(owner.Id))).Records);
        Assert.DoesNotContain(capture.Reads, sql => sql.Contains("CompressedPayload", StringComparison.Ordinal));

        byte[] malformed = [2, 0];
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivityRecordEffortSummaries\" SET \"ValuesBlob\" = {malformed} WHERE \"ActivityId\" = {rows[4].Id.ToString("D")}");
        await db.ActivityRecordEffortSummaries.Where(summary => summary.ActivityId == rows[3].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(summary => summary.ComputationVersion, 0));
        await db.ActivityRecordEffortSummaries.Where(summary => summary.ActivityId == rows[2].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(summary => summary.Sport, SportKind.Cycling));
        await db.ActivityRecordEffortSummaries.Where(summary => summary.ActivityId == rows[1].Id).ExecuteDeleteAsync();
        capture.Reads.Clear();
        Assert.Equal(live.Records, (await service.GetOverviewAsync(Query(owner.Id))).Records);
        Assert.Contains(capture.Reads, sql => sql.Contains("CompressedPayload", StringComparison.Ordinal) && sql.Contains("@pendingIds", StringComparison.Ordinal));

        var replacement = Payload(350);
        await db.ActivityStreams.Where(stream => stream.ActivityId == rows[0].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(stream => stream.CompressedPayload, replacement));
        Assert.False(await db.ActivityRecordEffortSummaries.AnyAsync(summary => summary.ActivityId == rows[0].Id));
        var fresh = await service.GetOverviewAsync(Query(owner.Id));
        Assert.Equal(350, Assert.Single(fresh.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").Value);
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
        await db.Activities.Where(activity => activity.Id == rows[0].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.Sport, SportKind.Cycling));
        Assert.False(await db.ActivityRecordEffortSummaries.AnyAsync(summary => summary.ActivityId == rows[0].Id));
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
        Assert.Equal(SportKind.Cycling, await db.ActivityRecordEffortSummaries.Where(summary => summary.ActivityId == rows[0].Id)
            .Select(summary => summary.Sport).SingleAsync());
        await db.ActivityStreams.Where(stream => stream.ActivityId == rows[0].Id).ExecuteDeleteAsync();
        Assert.False(await db.ActivityRecordEffortSummaries.AnyAsync(summary => summary.ActivityId == rows[0].Id));
    }

    [Fact]
    public void Compact_values_preserve_numeric_bits_and_reject_malformed_payloads()
    {
        const SportKind sport = SportKind.Running;
        var points = Enumerable.Range(0, 100).Select(index => new TrackPoint(Start.AddSeconds(index),
            55, 12, index * 3, null, null, null, null, 200 + index % 3 * .1, null)).ToArray();
        var expected = BestEffortCalculator.BestValues(points, sport, RecordEffortReadModel.GroupsFor(sport));
        Assert.True(expected.Count >= 2);
        var payload = RecordEffortReadModel.Build(points, sport, default);
        var actual = RecordEffortReadModel.Read(payload, sport)!;
        Assert.Equal(expected.Count, actual.Length);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Benchmark, actual[index].Benchmark);
            var before = expected[index].Value;
            var after = actual[index].Value;
            Assert.Equal(BitConverter.DoubleToInt64Bits(before.Value), BitConverter.DoubleToInt64Bits(after.Value));
            Assert.Equal(BitConverter.DoubleToInt64Bits(before.CoveragePercent), BitConverter.DoubleToInt64Bits(after.CoveragePercent));
            Assert.Equal(BitConverter.DoubleToInt64Bits(before.StartPosition), BitConverter.DoubleToInt64Bits(after.StartPosition));
            Assert.Equal(BitConverter.DoubleToInt64Bits(before.FinishPosition), BitConverter.DoubleToInt64Bits(after.FinishPosition));
        }

        Assert.Empty(RecordEffortReadModel.Read(RecordEffortReadModel.Build([], sport, default), sport)!);
        Assert.Null(RecordEffortReadModel.Read(null, sport));
        Assert.Null(RecordEffortReadModel.Read([1], sport));
        Assert.Null(RecordEffortReadModel.Read(payload[..^1], sport));
        var duplicate = payload.ToArray();
        const int entrySize = sizeof(ushort) + 4 * sizeof(double);
        payload.AsSpan(1, sizeof(ushort)).CopyTo(duplicate.AsSpan(1 + entrySize));
        Assert.Null(RecordEffortReadModel.Read(duplicate, sport));
        var nonfinite = payload.ToArray();
        BinaryPrimitives.WriteDoubleLittleEndian(nonfinite.AsSpan(1 + sizeof(ushort)), double.PositiveInfinity);
        Assert.Null(RecordEffortReadModel.Read(nonfinite, sport));
        var signedZero = payload.ToArray();
        BinaryPrimitives.WriteDoubleLittleEndian(signedZero.AsSpan(1 + sizeof(ushort) + 2 * sizeof(double)),
            BitConverter.Int64BitsToDouble(long.MinValue));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(RecordEffortReadModel.Read(signedZero, sport)![0].Value.StartPosition));
    }

    [Fact]
    public async Task Dense_summary_reads_use_covering_keys_then_integer_rowid_payload_lookups()
    {
        var capture = new Capture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Dense derived records", TimeZoneId = "UTC" };
        await using var db = factory.CreateDbContext();
        db.Add(owner);
        db.Activities.AddRange(Enumerable.Range(0, 5).Select(index => Row(owner.Id, index)));
        db.Activities.AddRange(Enumerable.Range(0, 1019).Select(index => new Activity
        {
            OwnerId = owner.Id,
            Sport = SportKind.Running,
            StartTimeUtc = Start,
            Title = "No stream",
            NaturalFingerprint = $"dense-scalar-{index}"
        }));
        await db.SaveChangesAsync();
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);

        capture.Commands.Clear();
        var result = await new StatisticsReadService(factory).GetOverviewAsync(Query(owner.Id) with { Sport = null });
        Assert.Equal(204, Assert.Single(result.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").Value);
        var keys = Assert.Single(capture.Commands, read => read.Sql.Contains("rowid AS \"RowId\" FROM \"ActivityRecordEffortSummaries\"", StringComparison.Ordinal));
        var payload = Assert.Single(capture.Commands, read => read.Sql.Contains("@effortRowIds", StringComparison.Ordinal));
        Assert.Contains(await ExplainAsync(db, keys), step => step.Contains("USING COVERING INDEX sqlite_autoindex_ActivityRecordEffortSummaries", StringComparison.Ordinal));
        Assert.Contains(await ExplainAsync(db, payload), step => step.Contains("USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dense_projection_reads_one_owner_sport_date_range_and_falls_back_after_stream_replacement()
    {
        var capture = new Capture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Clustered records", TimeZoneId = "Europe/Copenhagen" };
        var streams = Enumerable.Range(0, 5).Select(index => Row(owner.Id, index)).ToArray();
        var lowerBound = new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var upperBound = new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero);
        var wholeWinner = Streamless(1018, lowerBound, 2000);
        await using var db = factory.CreateDbContext();
        db.Add(owner);
        db.AddRange(streams);
        db.Activities.AddRange(Enumerable.Range(0, 1018).Select(index => Streamless(index, Start, 0)));
        db.Activities.AddRange(wholeWinner, Streamless(1019, upperBound, 3000));
        var indoor = Streamless(1020, Start, 4000);
        indoor.IsIndoor = true;
        db.Add(indoor);
        await db.SaveChangesAsync();

        var query = Query(owner.Id) with { Scope = RecordScope.Outdoor };
        var service = new StatisticsReadService(factory);
        var live = await service.GetOverviewAsync(query);
        Assert.Equal(wholeWinner.Id, Assert.Single(live.Records, record => record.Kind == RecordKind.Distance).ActivityId);
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
        await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);

        capture.Commands.Clear();
        Assert.Equal(live.Records, (await service.GetOverviewAsync(query)).Records);
        var range = Assert.Single(capture.Commands, command => command.Sql.Contains("FROM \"ActivityPeriodRecordRows\"", StringComparison.Ordinal));
        Assert.Contains(await ExplainAsync(db, range), step => step.Contains("USING PRIMARY KEY (OwnerId=? AND Sport=? AND StartTimeUtc>? AND StartTimeUtc<?)", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, command => command.Sql.Contains("CompressedPayload", StringComparison.Ordinal) ||
            command.Sql.Contains("FROM \"ActivityRecordEffortSummaries\"", StringComparison.Ordinal));

        const string title = "Updated streamless winner";
        await db.Activities.Where(activity => activity.Id == wholeWinner.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(activity => activity.Title, title));
        var refreshed = await service.GetOverviewAsync(query);
        Assert.Equal(title, Assert.Single(refreshed.Records, record => record.Kind == RecordKind.Distance).ActivityTitle);

        var replacement = Payload(350);
        await db.ActivityStreams.Where(stream => stream.ActivityId == streams[0].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(stream => stream.CompressedPayload, replacement));
        capture.Commands.Clear();
        var replaced = await service.GetOverviewAsync(query);
        Assert.Equal(350, Assert.Single(replaced.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").Value);
        Assert.Contains(capture.Commands, command => command.Sql.Contains("CompressedPayload", StringComparison.Ordinal));

        Activity Streamless(int index, DateTimeOffset start, double distance) => new()
        {
            OwnerId = owner.Id,
            Sport = SportKind.Running,
            StartTimeUtc = start,
            Title = "No stream",
            DistanceMeters = distance,
            NaturalFingerprint = $"clustered-scalar-{index}"
        };
    }

    [Fact]
    public async Task Read_only_legacy_schema_without_effort_table_keeps_live_period_results()
    {
        var factory = await Database(new());
        var owner = new OwnerProfile { DisplayName = "Read-only legacy records", TimeZoneId = "UTC" };
        Factory readOnly;
        await using (var db = factory.CreateDbContext())
        {
            db.AddRange(owner, Row(owner.Id, 1));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ActivityRecordEffortSummaries\"");
            var connectionString = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()) { Mode = SqliteOpenMode.ReadOnly };
            readOnly = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connectionString.ToString()).Options);
        }
        await new ReadOnlyDatabaseValidator(readOnly).ValidateAsync();
        var result = await new StatisticsReadService(readOnly).GetOverviewAsync(Query(owner.Id));
        Assert.Equal(201, Assert.Single(result.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s").Value);
    }

    private static RecordOverviewQuery Query(Guid owner) => new(owner, SportKind.Running, RecordScope.All,
        new(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 31)), Start);

    private static Activity Row(Guid owner, int index) => new()
    {
        OwnerId = owner,
        Sport = SportKind.Running,
        StartTimeUtc = Start.AddSeconds(index),
        Title = $"Activity {index}",
        NaturalFingerprint = $"effort-read-model-{index}",
        DistanceMeters = 1000 + index,
        MovingTimeSeconds = 100,
        Stream = new() { OwnerId = owner, PointCount = 3, CompressedPayload = Payload(index == 0 ? -100 : 200 + index) }
    };

    private static byte[] Payload(double watts) => TrackCodec.Encode(Enumerable.Range(0, 3).Select(index => new TrackPoint(
        Start.AddSeconds(index * 5), 55, 12, index * 30, null, null, null, null, watts, null)).ToArray());

    private static async Task<string[]> ExplainAsync(ExplorerDbContext db, ReadCommand read)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + read.Sql;
        foreach (var value in read.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.DbType = value.Type;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return details.ToArray();
    }

    private static async Task<Factory> Database(Capture capture)
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "effort-read-model.db");
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False")
            .AddInterceptors(capture).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed record ReadCommand(string Sql, (string Name, DbType Type, object? Value)[] Parameters);

    private sealed class Capture : DbCommandInterceptor
    {
        private int backfillReads;
        public CancellationTokenSource? CancelSecondBackfillRead { get; set; }
        public List<string> Reads { get; } = [];
        public List<ReadCommand> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads.Add(command.CommandText);
            Commands.Add(new(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(parameter => (parameter.ParameterName, parameter.DbType, parameter.Value)).ToArray()));
            if (command.CommandText.Contains("ORDER BY s.rowid LIMIT", StringComparison.Ordinal) && ++backfillReads == 2)
                CancelSecondBackfillRead?.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Import_commits_canonical_stream_and_derived_efforts_atomically()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var oldRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        try
        {
            var owner = await setup.SeedOwnerAsync("Atomic effort import");
            var storage = CreateStorageServices(setup);
            var paths = new AppDataPaths();
            var importer = new ImportProcessor(setup.Factory, [new MetadataImporter()], paths, storage.Originals, storage.FileOperations,
                storage.OwnerLock, new StatisticsService(setup.Factory),
                new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), NullLogger<ImportProcessor>.Instance);

            await using var db = await setup.Factory.CreateDbContextAsync();
            await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER "FailRecordEffortInsert" BEFORE INSERT ON "ActivityRecordEffortSummaries"
                BEGIN SELECT RAISE(ABORT, 'Synthetic effort write failure'); END;
                """);
            var failed = await AddBatchAsync();
            await importer.ProcessAsync(failed);
            Assert.Equal(ImportStatus.Failed, await db.ImportBatches.Where(batch => batch.Id == failed).Select(batch => batch.Status).SingleAsync());
            Assert.Empty(await db.Activities.ToArrayAsync());
            Assert.Empty(await db.ActivityStreams.ToArrayAsync());
            Assert.Empty(await db.SourceFiles.ToArrayAsync());
            Assert.Equal(0L, await db.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM ActivityPeriodRecordRows").SingleAsync());

            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER \"FailRecordEffortInsert\"");
            var succeeded = await AddBatchAsync();
            await importer.ProcessAsync(succeeded);
            var stream = await db.ActivityStreams.SingleAsync();
            var derived = await db.ActivityRecordEffortSummaries.SingleAsync();
            Assert.Equal(stream.ActivityId, derived.ActivityId);
            Assert.NotNull(RecordEffortReadModel.Read(derived.ValuesBlob, derived.Sport));
            var projection = await db.Database.SqlQueryRaw<PeriodRecordProjectionStore.Row>("""
                SELECT "OwnerId", "Sport", "StartTimeUtc" AS "StartTicks", "ActivityId", "Title",
                    "DistanceMeters", "MovingTimeSeconds", "ElevationGainMeters", "IsIndoor", "HasStream",
                    "SummarySport", "ComputationVersion", "ValuesBlob"
                FROM "ActivityPeriodRecordRows"
                """).SingleAsync();
            Assert.Equal(stream.ActivityId, projection.ActivityId);
            Assert.True(projection.HasStream);
            Assert.Equal(derived.Sport, projection.SummarySport);
            Assert.Equal(derived.ComputationVersion, projection.ComputationVersion);
            Assert.Equal(derived.ValuesBlob, projection.ValuesBlob);

            async Task<Guid> AddBatchAsync()
            {
                var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var staged = Path.Combine(directory, "activity.fit");
                await File.WriteAllTextAsync(staged, "synthetic effort read model import");
                await using var staging = await setup.Factory.CreateDbContextAsync();
                var batch = new ImportBatch
                {
                    OwnerId = owner,
                    SourceKind = SourceKind.GarminArchive,
                    DisplayName = "Atomic effort import",
                    StagedPath = staged,
                    Status = ImportStatus.Queued
                };
                staging.Add(batch);
                await staging.SaveChangesAsync();
                return batch.Id;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", oldRoot);
        }
    }
}

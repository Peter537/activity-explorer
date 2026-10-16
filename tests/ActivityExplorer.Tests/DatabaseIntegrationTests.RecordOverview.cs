using System.Data.Common;
using Stopwatch = System.Diagnostics.Stopwatch;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData(SportKind.Cycling, 10)]
    [InlineData(SportKind.Running, 5)]
    [InlineData(SportKind.Walking, 3)]
    [InlineData(SportKind.Rowing, 3)]
    public async Task Period_overview_recalculates_every_category_inside_the_period_with_attempt_tie_parity(SportKind sport, double speed)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Period records owner");
        var other = await setup.SeedOwnerAsync("Other period records owner");
        var start = ReportingInstant("2026-01-02T12:00:00Z");
        var winner = OverviewActivity(owner, sport, start, speed, 300);
        winner.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var sameDateTie = OverviewActivity(owner, sport, start, speed, 300);
        sameDateTie.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var laterTie = OverviewActivity(owner, sport, start.AddDays(1), speed, 300);
        var lifetime = OverviewActivity(owner, sport, start.AddYears(-1), speed * 1.5, 500);
        lifetime.MovingTimeSeconds = 1200;
        var indoor = OverviewActivity(owner, sport, start, speed * 2, 900); indoor.IsIndoor = true;
        var foreign = OverviewActivity(other, sport, start, speed * 2, 900);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(winner, sameDateTie, laterTie, lifetime, indoor, foreign);
            await db.SaveChangesAsync();
        }
        var statistics = new StatisticsService(setup.Factory);
        await statistics.RecomputeAsync(owner);
        var snapshots = await statistics.GetRecordsAsync(owner, RecordScope.Outdoor);
        Assert.Equal(lifetime.Id, snapshots.Single(record => record.Kind == RecordKind.Distance).ActivityId);
        var query = new RecordOverviewQuery(owner, sport, RecordScope.Outdoor,
            new(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 31)));
        var overview = await statistics.GetOverviewAsync(query);
        Assert.NotEmpty(overview.Records);
        Assert.All(overview.Records, record => Assert.Equal(winner.Id, record.ActivityId));
        var kinds = Enum.GetValues<RecordKind>().Where(kind => sport != SportKind.Rowing || kind != RecordKind.Elevation).Order();
        Assert.Equal(kinds, overview.Records.Select(record => record.Kind).Distinct().Order());
        foreach (var representative in overview.Records.GroupBy(record => record.Kind).Select(group => group.First()))
        {
            var attempts = await statistics.GetAttemptsAsync(new(sport, representative.Kind, representative.Key, owner,
                RecordScope.Outdoor, Period: query.Period));
            var best = attempts!.Attempts.Items[0];
            Assert.Equal(representative.ActivityId, best.ActivityId);
            Assert.Equal(representative.Value, best.Value, 8);
            Assert.Equal(representative.CoveragePercent, best.CoveragePercent, 8);
        }
        Assert.Equal(snapshots, await statistics.GetRecordsAsync(owner, RecordScope.Outdoor));
        Assert.Equal(overview.Records, (await statistics.GetOverviewAsync(query)).Records);
        var allTime = await statistics.GetOverviewAsync(query with { Period = new(ReportingPreset.AllTime) });
        Assert.Equal(snapshots, allTime.Records);
        var indoorOverview = await statistics.GetOverviewAsync(query with { Scope = RecordScope.Indoor });
        Assert.All(indoorOverview.Records, record => Assert.Equal(indoor.Id, record.ActivityId));
        var allOwners = await statistics.GetOverviewAsync(query with { OwnerId = null });
        Assert.Equal(new[] { owner, other }.Order(), allOwners.Records.Where(record => record.Kind == RecordKind.Distance)
            .Select(record => record.OwnerId).Order());
    }

    [Fact]
    public async Task Period_overview_reads_large_histories_in_bounded_batches_instead_of_per_benchmark()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Measured overview");
        var start = ReportingInstant("2026-04-01T12:00:00Z");
        var payload = TrackCodec.Encode(OverviewPoints(start, 3, 200, 300));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 128; index++)
            {
                var activity = OverviewActivity(owner, SportKind.Running, start, 3, 200, 300, payload: payload);
                db.Activities.Add(activity);
            }
            for (var index = 0; index < 40; index++)
            {
                var outside = OverviewActivity(owner, SportKind.Running, start.AddYears(-1), 3, 200, 300, payload: [0, 1, 2]);
                db.Activities.Add(outside);
            }
            await db.SaveChangesAsync();
        }
        var capture = new OverviewReadCapture();
        var factory = new TestDbFactory(new DbContextOptionsBuilder<Infrastructure.Storage.ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var statistics = new StatisticsService(factory);
        var allocated = GC.GetTotalAllocatedBytes();
        var stopwatch = Stopwatch.StartNew();
        var result = await statistics.GetOverviewAsync(new(owner, SportKind.Running, Period:
            new(ReportingPreset.Custom, new(2026, 4, 1), new(2026, 4, 30))));
        stopwatch.Stop();
        allocated = GC.GetTotalAllocatedBytes() - allocated;
        TestContext.Current.TestOutputHelper!.WriteLine($"Period overview: 128 eligible activities × 301 samples, 40 excluded corrupt streams; {stopwatch.Elapsed.TotalMilliseconds:N0} ms, {allocated:N0} process-wide allocated bytes; {capture.StreamReads.Count} bounded payload queries.");
        Assert.NotEmpty(result.Records);
        Assert.Equal(9, capture.StreamReads.Count);
        Assert.All(capture.StreamReads, read =>
        {
            Assert.Contains("LIMIT", read.Sql, StringComparison.Ordinal);
            Assert.Contains(read.Values, value => value is int number && number == 16);
            Assert.DoesNotContain("GeometryWkb", read.Sql, StringComparison.Ordinal);
            Assert.NotNull(read.Transaction);
            Assert.Same(capture.StreamReads[0].Transaction, read.Transaction);
        });
        Assert.Equal(0, capture.Writes);
        capture.StreamReads.Clear();
        await statistics.GetOverviewAsync(new(owner, SportKind.Running));
        Assert.Empty(capture.StreamReads);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.StatisticSnapshots.ToArrayAsync());
    }

    [Fact]
    public async Task Cancelled_period_scans_discard_partial_results_and_can_be_retried_immediately()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Cancelled overview");
        var start = ReportingInstant("2026-04-01T12:00:00Z");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 33; index++) db.Activities.Add(OverviewActivity(owner, SportKind.Running, start, 3, 200, 30));
            await db.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var capture = new OverviewReadCapture { CancelAt = 2, Cancellation = cancellation };
        var factory = new TestDbFactory(new DbContextOptionsBuilder<Infrastructure.Storage.ExplorerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
        var statistics = new StatisticsService(factory);
        var query = new RecordOverviewQuery(owner, SportKind.Running, Period: new(ReportingPreset.ThisMonth), AsOfUtc: start);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => statistics.GetOverviewAsync(query, cancellation.Token));
        Assert.Equal(2, capture.StreamReads.Count);
        capture.CancelAt = 0;
        Assert.NotEmpty((await statistics.GetOverviewAsync(query)).Records);
        Assert.Equal(0, capture.Writes);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Empty(await verification.StatisticSnapshots.ToArrayAsync());
    }

    private static Activity OverviewActivity(Guid owner, SportKind sport, DateTimeOffset start, double speed, double power,
        int seconds = 600, bool gps = true, byte[]? payload = null) => new()
        {
            OwnerId = owner,
            Title = $"Overview {sport}",
            Sport = sport,
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            StartTimeUtc = start,
            DistanceMeters = speed * seconds,
            MovingTimeSeconds = seconds,
            ElapsedTimeSeconds = seconds,
            ElevationGainMeters = 100,
            HasGps = gps,
            HasPower = true,
            Stream = new() { OwnerId = owner, PointCount = seconds + 1, CompressedPayload = payload ?? TrackCodec.Encode(OverviewPoints(start, speed, power, seconds, gps)) }
        };

    private static TrackPoint[] OverviewPoints(DateTimeOffset start, double speed, double power, int seconds, bool gps = true) =>
        Enumerable.Range(0, seconds + 1).Select(index => new TrackPoint(start.AddSeconds(index), gps ? 0 : null,
            gps ? index * speed / 111_195 : null, index * speed, null, speed, null, null, power, null)).ToArray();

    private sealed class OverviewReadCapture : DbCommandInterceptor
    {
        public List<(string Sql, DbTransaction? Transaction, object?[] Values)> StreamReads { get; } = [];
        public int CancelAt { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }
        public int Writes { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ActivityStreams", StringComparison.Ordinal))
            {
                StreamReads.Add((command.CommandText, command.Transaction, command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).ToArray()));
                if (CancelAt > 0 && StreamReads.Count == CancelAt) Cancellation?.Cancel();
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.FromResult(result);
        }
    }
}

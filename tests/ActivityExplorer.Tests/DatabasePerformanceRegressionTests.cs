using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Activity = ActivityExplorer.Core.Domain.Activity;

namespace ActivityExplorer.Tests;

public sealed class DatabasePerformanceRegressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Record_scans_keep_all_tied_rows_and_owner_sport_scope_and_period_membership()
    {
        var capture = new ReadCapture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Primary records", TimeZoneId = "UTC" };
        var other = new OwnerProfile { DisplayName = "Other records", TimeZoneId = "UTC" };
        var beforePeriod = Row(owner.Id, 200);
        beforePeriod.StartTimeUtc = Start.AddDays(-1);
        var afterPeriod = Row(owner.Id, 201);
        afterPeriod.StartTimeUtc = Start.AddDays(7);
        var rows = Enumerable.Range(0, 160).Select(index => Row(owner.Id, index))
            .Concat(Enumerable.Range(160, 40).Select(index => Row(other.Id, index)))
            .Append(beforePeriod).Append(afterPeriod).ToArray();
        await using (var db = factory.CreateDbContext())
        {
            db.AddRange(owner, other);
            db.Activities.AddRange(rows);
            await db.SaveChangesAsync();
        }
        capture.Reads.Clear();
        var service = new StatisticsReadService(factory);
        var period = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 7));
        foreach (var ownerId in new Guid?[] { owner.Id, null })
            foreach (var sport in new[] { SportKind.Cycling, SportKind.Running })
                foreach (var scope in new[] { RecordScope.All, RecordScope.Indoor, RecordScope.Outdoor })
                {
                    var eligible = rows.Where(row => (!ownerId.HasValue || row.OwnerId == ownerId) && row.Sport == sport &&
                        (scope == RecordScope.All || row.IsIndoor == (scope == RecordScope.Indoor)) &&
                        row.StartTimeUtc >= Start.AddHours(-12) && row.StartTimeUtc < Start.AddDays(7).AddHours(-12)).ToArray();
                    var expected = eligible.OrderByDescending(row => row.DistanceMeters).ThenBy(row => row.StartTimeUtc).ThenBy(row => row.Id).ToArray();
                    var first = (await service.GetAttemptsAsync(new(sport, RecordKind.Distance, "Longest distance", ownerId, scope, Period: period)))!;
                    var second = (await service.GetAttemptsAsync(new(sport, RecordKind.Distance, "Longest distance", ownerId, scope, Page: 2, Period: period)))!;
                    Assert.Equal(expected.Length, first.Attempts.Total);
                    Assert.Equal(expected.Take(50).Select(row => row.Id), first.Attempts.Items.Select(item => item.ActivityId));
                    if (expected.Length > 50)
                        Assert.Equal(expected.Skip(50).Take(50).Select(row => row.Id), second.Attempts.Items.Select(item => item.ActivityId));
                    var history = (await service.GetHistoryAsync(new(sport, RecordKind.Distance, "Longest distance", ownerId, scope, period)))!;
                    Assert.Equal(eligible.Select(row => row.Id).Order(), history.Series.SelectMany(series => series.Points).Select(point => point.Attempt.ActivityId).Order());
                    var overview = await service.GetOverviewAsync(new(ownerId, sport, scope, period));
                    foreach (var group in eligible.GroupBy(row => row.OwnerId))
                    {
                        var winner = group.OrderByDescending(row => row.DistanceMeters).ThenBy(row => row.StartTimeUtc).ThenBy(row => row.Id).First();
                        Assert.Equal(winner.Id, overview.Records.Single(record => record.OwnerId == group.Key && record.Kind == RecordKind.Distance).ActivityId);
                    }
                }
        Assert.All(capture.Reads, read => Assert.DoesNotContain("OFFSET", read.Sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Whole_activity_attempts_and_history_read_summaries_once_and_keep_nonfinite_eligibility()
    {
        var capture = new ReadCapture();
        var factory = await Database(capture);
        var owner = new OwnerProfile { DisplayName = "Scalar records", TimeZoneId = "UTC" };
        var rows = Enumerable.Range(0, 80).Select(index =>
        {
            var row = Row(owner.Id, index);
            row.Sport = SportKind.Cycling;
            row.MovingTimeSeconds = index % 9 == 0 ? 0 : index % 19 == 0 ? double.PositiveInfinity : 100;
            row.DistanceMeters = index % 13 == 0 ? double.PositiveInfinity : index % 11 == 0 ? 999 : index % 23 == 0 ? -1 : 1_000 + index % 3 * 100;
            row.ElevationGainMeters = index % 17 == 0 ? double.PositiveInfinity : 10;
            if (index % 29 == 0)
            {
                row.DistanceMeters = double.MaxValue;
                row.MovingTimeSeconds = 0.25;
            }
            row.Stream = new() { OwnerId = owner.Id, PointCount = 1, CompressedPayload = [0, 1, 2] };
            return row;
        }).ToArray();
        await using (var db = factory.CreateDbContext())
        {
            db.Add(owner);
            db.Activities.AddRange(rows);
            await db.SaveChangesAsync();
        }
        var service = new StatisticsReadService(factory);
        foreach (var (kind, key) in new[]
        {
            (RecordKind.Distance, "Longest distance"), (RecordKind.Duration, "Longest moving time"),
            (RecordKind.Elevation, "Most elevation gain"), (RecordKind.AverageSpeed, "Best average speed (activities >= 1 km)")
        })
        {
            var expected = rows.Select(row => (Row: row, Value: kind switch
            {
                RecordKind.Distance => row.DistanceMeters,
                RecordKind.Duration => row.MovingTimeSeconds,
                RecordKind.Elevation => row.ElevationGainMeters,
                RecordKind.AverageSpeed when row.DistanceMeters >= 1_000 && row.MovingTimeSeconds > 0 => row.DistanceMeters / row.MovingTimeSeconds,
                _ => 0
            }))
                .Where(item => double.IsFinite(item.Value) && item.Value > 0)
                .OrderByDescending(item => item.Value).ThenBy(item => item.Row.StartTimeUtc).ThenBy(item => item.Row.Id).ToArray();
            capture.Reads.Clear();
            var attempts = (await service.GetAttemptsAsync(new(SportKind.Cycling, kind, key, owner.Id, Page: int.MaxValue)))!;
            Assert.Equal(expected.Length, attempts.Attempts.Total);
            var lastPage = (expected.Length + 49) / 50;
            Assert.Equal(lastPage, attempts.Attempts.Page);
            Assert.Equal(expected.Skip((lastPage - 1) * 50).Select(item => item.Row.Id), attempts.Attempts.Items.Select(item => item.ActivityId));
            Assert.InRange(capture.Reads.Count, 1, 2);
            Assert.All(capture.Reads, read => Assert.DoesNotContain("ActivityStreams", read.Sql, StringComparison.Ordinal));
            capture.Reads.Clear();
            var history = (await service.GetHistoryAsync(new(SportKind.Cycling, kind, key, owner.Id)))!;
            var points = Assert.Single(history.Series).Points;
            var chronological = expected.OrderBy(item => item.Row.StartTimeUtc).ThenBy(item => item.Row.Id).ToArray();
            Assert.Equal(chronological.Select(item => item.Row.Id), points.Select(point => point.Attempt.ActivityId));
            Assert.Equal(chronological.Select(item => item.Value), points.Select(point => point.Attempt.Value));
            Assert.InRange(capture.Reads.Count, 1, 2);
            Assert.All(capture.Reads, read => Assert.DoesNotContain("ActivityStreams", read.Sql, StringComparison.Ordinal));
        }
    }

    private static Activity Row(Guid owner, int index) => new()
    {
        Id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"performance-row-{index}")).AsSpan(0, 16)),
        OwnerId = owner,
        Title = $"Synthetic record {index}",
        NaturalFingerprint = $"performance-row-{index}",
        Sport = index % 5 == 0 ? SportKind.Running : SportKind.Cycling,
        IsIndoor = index % 2 == 0,
        StartTimeUtc = Start.AddDays(index % 7),
        DistanceMeters = 1_000 + index % 3 * 100,
        MovingTimeSeconds = 100,
        ElevationGainMeters = 10
    };

    private static async Task<Factory> Database(ReadCapture capture)
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "performance-regression.db");
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={path}").AddInterceptors(capture).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private sealed record ReadCommand(string Sql);
    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<ReadCommand> Reads { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads.Add(new(command.CommandText));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}

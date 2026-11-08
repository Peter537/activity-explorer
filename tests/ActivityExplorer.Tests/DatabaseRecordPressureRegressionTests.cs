using System.Data.Common;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

public sealed class DatabaseRecordPressureRegressionTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task Overview_scans_only_eligible_streams_and_keeps_scalar_winners(bool dense, bool manyStreamless, bool lowerCase)
    {
        var capture = new ReadCapture();
        var path = Path.Combine(TestSupport.NewDirectory(), "adaptive-overview.db");
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(capture).Options);
        var owner = new OwnerProfile { DisplayName = "Eligible", TimeZoneId = "UTC" };
        var other = new OwnerProfile { DisplayName = "Other", TimeZoneId = "UTC" };
        var start = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        var scalarWinner = Activity(owner.Id, start, "Scalar winner");
        scalarWinner.DistanceMeters = 999_999;
        var streamWinners = Enumerable.Range(0, 5).Select(index =>
        {
            var activity = Activity(owner.Id, start.AddSeconds(index), $"Power {index}");
            var points = Enumerable.Range(0, 3).Select(point => new TrackPoint(activity.StartTimeUtc.AddSeconds(point * 5),
                55, 12 + point * .0001, point * 30, 10, 6, 140, 80, 200 + index, 15)).ToArray();
            if (index == 4) activity.Id = Guid.Parse("abcdef12-abcd-abcd-abcd-abcdef123456");
            activity.Stream = new ActivityStream { OwnerId = owner.Id, PointCount = points.Length, CompressedPayload = TrackCodec.Encode(points) };
            return activity;
        }).ToArray();
        var excluded = new[]
        {
            Activity(other.Id, start, "Different owner"),
            Activity(owner.Id, start, "Different sport"),
            Activity(owner.Id, start.AddYears(-1), "Different period"),
            Activity(owner.Id, start, "Indoor")
        };
        excluded[1].Sport = SportKind.Cycling;
        excluded[3].IsIndoor = true;
        foreach (var activity in excluded)
            activity.Stream = new ActivityStream { OwnerId = activity.OwnerId, CompressedPayload = [0, 1, 2], PointCount = 3 };
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.Owners.AddRange(owner, other);
            db.Activities.Add(scalarWinner);
            db.Activities.AddRange(streamWinners);
            db.Activities.AddRange(excluded);
            if (dense || manyStreamless)
            {
                // 1,025 eligible rows with five streams, including an odd final payload batch.
                db.Activities.AddRange(Enumerable.Range(0, 1019).Select(i => Activity(owner.Id, start, $"No stream {i}")));
            }
            if (!dense)
            {
                // Many foreign corrupt streams must never be decoded, even with many streamless eligible rows.
                db.Activities.AddRange(Enumerable.Range(0, 1025).Select(i =>
                {
                    var activity = Activity(other.Id, start, $"Excluded {i}");
                    activity.Stream = new ActivityStream { OwnerId = other.Id, CompressedPayload = [0, 1, 2], PointCount = 3 };
                    return activity;
                }));
            }
            await db.SaveChangesAsync();
            if (lowerCase)
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys = ON");
                var id = streamWinners[^1].Id;
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Activities\" SET \"Id\" = lower(\"Id\") WHERE \"Id\" = {id}");
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"ActivityStreams\" SET \"ActivityId\" = lower(\"ActivityId\") WHERE \"ActivityId\" = {id}");
                await transaction.CommitAsync();
            }
        }
        capture.Commands.Clear();
        var service = new StatisticsReadService(factory);
        var result = await service.GetOverviewAsync(new(owner.Id, SportKind.Running, RecordScope.Outdoor,
            new(ReportingPreset.Custom, new(2026, 1, 1), new(2026, 1, 31)), start));
        Assert.Equal(scalarWinner.Id, Assert.Single(result.Records, record => record.Kind == RecordKind.Distance).ActivityId);
        var power = Assert.Single(result.Records, record => record.Kind == RecordKind.PowerCurve && record.Key == "5 s");
        Assert.Equal(streamWinners[^1].Id, power.ActivityId);
        Assert.Equal(204, power.Value);
        var payloadRead = Assert.Single(capture.Commands, command => command.Contains("CompressedPayload", StringComparison.Ordinal));
        Assert.DoesNotContain("NOT INDEXED", payloadRead, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" IN (", payloadRead, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT", payloadRead, StringComparison.OrdinalIgnoreCase);
        var physicalLookup = dense || manyStreamless;
        Assert.Equal(physicalLookup, payloadRead.Contains("@physicalKeys", StringComparison.Ordinal));
        if (physicalLookup)
        {
            Assert.Contains("rowid", payloadRead, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("json_each", payloadRead, StringComparison.OrdinalIgnoreCase);
            await using var verification = factory.CreateDbContext();
            await verification.Database.OpenConnectionAsync();
            await using var explain = verification.Database.GetDbConnection().CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + payloadRead;
            foreach (var (name, value, type) in capture.PayloadParameters)
            {
                var parameter = explain.CreateParameter();
                parameter.ParameterName = name;
                parameter.DbType = type;
                parameter.Value = value ?? DBNull.Value;
                explain.Parameters.Add(parameter);
            }
            await using var reader = await explain.ExecuteReaderAsync();
            var plan = new List<string>();
            while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            Assert.Contains(plan, step => step.Contains("SEARCH", StringComparison.OrdinalIgnoreCase) &&
                step.Contains("USING INTEGER PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
        }
        Assert.All(capture.Commands, command =>
        {
            Assert.DoesNotContain("OFFSET", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GeometryWkb", command, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Activity Activity(Guid owner, DateTimeOffset start, string title) => new()
    {
        OwnerId = owner,
        Title = title,
        Sport = SportKind.Running,
        StartTimeUtc = start,
        NaturalFingerprint = Guid.NewGuid().ToString("N"),
        DistanceMeters = 1000,
        MovingTimeSeconds = 100,
        ElapsedTimeSeconds = 100
    };

    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public (string Name, object? Value, System.Data.DbType Type)[] PayloadParameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (command.CommandText.Contains("CompressedPayload", StringComparison.Ordinal))
                PayloadParameters = command.Parameters.Cast<DbParameter>().Select(value => (value.ParameterName, value.Value, value.DbType)).ToArray();
            return ValueTask.FromResult(result);
        }
    }
}

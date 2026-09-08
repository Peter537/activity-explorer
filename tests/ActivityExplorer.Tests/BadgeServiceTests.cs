using System.Data.Common;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed class BadgeServiceTests
{
    [Fact]
    public async Task Import_duplicate_transfer_delete_and_reimport_recalculate_without_award_storage()
    {
        await using var setup = await Setup.CreateAsync();
        var profiles = setup.Services.GetRequiredService<IProfileService>();
        var first = await profiles.CreateAsync("Badge athlete");
        var second = await profiles.CreateAsync("Other athlete");
        var input = TestSupport.RowingFit(setup.Root, durationSeconds: 1200);
        await setup.ImportAsync(first, input);
        var badges = setup.Services.GetRequiredService<IBadgeService>();
        var original = await badges.GetCatalogueAsync(first);
        Assert.True(original.EarnedCount > 0);
        Assert.Equal(BadgeStatus.Completed, BadgeTests.Badge(original, "rowing-lifetime-singledistance-2").Status);
        await setup.ImportAsync(first, input);
        Assert.Equal(original.Level, (await badges.GetCatalogueAsync(first)).Level);
        Assert.Equal(0, (await badges.GetCatalogueAsync(second)).EarnedCount);
        var id = original.Editions.SelectMany(x => x.Evidence).First().ActivityId;
        var activities = setup.Services.GetRequiredService<IActivityQueryService>();
        await activities.UpdateAsync(id, new UpdateActivityRequest("Transferred row", null, null, second));
        Assert.Equal(0, (await badges.GetCatalogueAsync(first)).EarnedCount);
        Assert.Equal(original.Level, (await badges.GetCatalogueAsync(second)).Level);
        var detail = await badges.GetDetailAsync(second, "rowing-lifetime-singledistance-2", "lifetime");
        Assert.NotNull(detail);
        Assert.Equal(id, Assert.Single(detail.Selected.Evidence).ActivityId);
        Assert.Contains(detail.Related, x => x.Definition.Id == "rowing-lifetime-singledistance-5");
        Assert.NotNull(await badges.GetDetailAsync(second, "rowing-lifetime-singledistance-2", null));
        Assert.Null(await badges.GetDetailAsync(second, "not-a-badge", null));
        Assert.Null(await badges.GetDetailAsync(second, "rowing-lifetime-singledistance-2", "wrong-edition"));
        await activities.DeleteAsync([id]);
        Assert.Equal(0, (await badges.GetCatalogueAsync(second)).Level.Points);
        await setup.ImportAsync(second, input);
        Assert.Equal(original.Level, (await badges.GetCatalogueAsync(second)).Level);
        await profiles.DeleteAsync(second, "DELETE Other athlete");
        Assert.Single(await badges.GetOverviewAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => badges.GetCatalogueAsync(second));
    }

    [Fact]
    public async Task Backdating_corrections_timezones_and_service_restart_use_current_summaries()
    {
        await using var setup = await Setup.CreateAsync();
        var profiles = setup.Services.GetRequiredService<IProfileService>();
        var owner = await profiles.CreateAsync("Historical athlete");
        var badges = setup.Services.GetRequiredService<IBadgeService>();
        var first = new Activity
        {
            OwnerId = owner,
            Sport = SportKind.Cycling,
            Title = "Fictional year-end ride",
            NaturalFingerprint = Guid.NewGuid().ToString(),
            StartTimeUtc = DateTimeOffset.Parse("2018-12-31T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DistanceMeters = 100000,
            MovingTimeSeconds = 3600,
            ElapsedTimeSeconds = 7200,
            IsIndoor = true
        };
        await using (var db = await setup.Factory.CreateDbContextAsync()) { db.Activities.Add(first); await db.SaveChangesAsync(); }
        var initial = await badges.GetCatalogueAsync(owner, new(2019, 1, 1));
        Assert.Equal(new DateOnly(2019, 1, 1), BadgeTests.Badge(initial, "new-year", "2019").EarnedOn);
        await profiles.UpdateTimeZoneAsync(owner, "UTC");
        var utc = await badges.GetCatalogueAsync(owner, new(2019, 1, 1));
        Assert.Equal(new DateOnly(2018, 12, 31), BadgeTests.Badge(utc, "year-end", "2018").EarnedOn);
        Assert.Equal(BadgeStatus.NotStarted, BadgeTests.Badge(utc, "new-year", "2019").Status);
        using var export = JsonDocument.Parse((await profiles.ExportAsync(owner)).Json);
        Assert.Equal("UTC", export.RootElement.GetProperty("profile").GetProperty("timeZoneId").GetString());
        Assert.Equal("UTC", Assert.Single(await profiles.ListAsync()).TimeZoneId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.UpdateTimeZoneAsync(owner, "Invalid/Zone"));
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.UpdateTimeZoneAsync(owner, ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.UpdateTimeZoneAsync(Guid.NewGuid(), "UTC"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var activity = await db.Activities.SingleAsync();
            activity.DistanceMeters = 4000;
            await db.SaveChangesAsync();
        }
        var restarted = new BadgeService(setup.Factory, new FixedTime());
        Assert.Null(BadgeTests.Badge(await restarted.GetCatalogueAsync(owner), "cycling-lifetime-singledistance-5").EarnedOn);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync()).TimeZoneId = "Missing/Zone";
            await db.SaveChangesAsync();
        }
        var overview = Assert.Single(await badges.GetOverviewAsync());
        Assert.Null(overview.Level);
        Assert.Contains("timezone", overview.Error);
        await profiles.UpdateTimeZoneAsync(owner, BadgeTimeZone.DefaultId);
        Assert.NotNull(Assert.Single(await badges.GetOverviewAsync()).Level);
    }

    [Fact]
    public async Task Warm_ten_thousand_activity_query_is_bounded_and_does_not_read_streams()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = await setup.Services.GetRequiredService<IProfileService>().CreateAsync("Performance athlete");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(Enumerable.Range(0, 10000).Select(i => new Activity
            {
                OwnerId = owner,
                Sport = (SportKind)(i % 4 + 1),
                Title = "Synthetic benchmark",
                NaturalFingerprint = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StartTimeUtc = new DateTimeOffset(2010, 1, 1, 10, 0, 0, TimeSpan.Zero).AddHours(i * 12),
                DistanceMeters = 20000,
                MovingTimeSeconds = 3600,
                ElevationGainMeters = 200
            }));
            await db.SaveChangesAsync();
        }
        var counter = new QueryCounter();
        var factory = new Factory(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={setup.Services.GetRequiredService<AppDataPaths>().DatabasePath}").AddInterceptors(counter).Options);
        var service = new BadgeService(factory, new FixedTime());
        await service.GetCatalogueAsync(owner);
        counter.Commands.Clear();
        var timer = Stopwatch.StartNew();
        var result = await service.GetCatalogueAsync(owner);
        timer.Stop();
        Assert.True(result.EarnedCount > 100);
        Assert.Equal(2, counter.Commands.Count);
        Assert.DoesNotContain(counter.Commands, x => x.Contains("ActivityStreams", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper!.WriteLine($"Warm query + evaluation: {timer.Elapsed.TotalMilliseconds:N1} ms, {counter.Commands.Count} queries");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Factory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
    }
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class Setup(ServiceProvider services, string root) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public string Root { get; } = root;
        public IDbContextFactory<ExplorerDbContext> Factory => Services.GetRequiredService<IDbContextFactory<ExplorerDbContext>>();
        public static async Task<Setup> CreateAsync()
        {
            var previous = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
            var root = TestSupport.NewDirectory();
            try
            {
                Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", root);
                var services = new ServiceCollection().AddLogging().AddActivityExplorer().AddSingleton<TimeProvider>(new FixedTime()).BuildServiceProvider();
                await services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
                return new(services, root);
            }
            finally { Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previous); }
        }
        public async Task ImportAsync(Guid owner, string input)
        {
            var staging = Path.Combine(Services.GetRequiredService<AppDataPaths>().StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var staged = Path.Combine(staging, Path.GetFileName(input));
            File.Copy(input, staged);
            var id = await Services.GetRequiredService<IImportQueue>().EnqueueAsync(new(owner, staged, Path.GetFileName(input), SourceKind.Fit));
            await Services.GetRequiredService<IImportProcessor>().ProcessAsync(id);
            await using var db = await Factory.CreateDbContextAsync();
            Assert.Equal(ImportStatus.Completed, (await db.ImportBatches.FindAsync(id))!.Status);
        }
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}

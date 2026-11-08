using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

// Opt-in, separate-process workload. Never opens the application's configured data directory.
public sealed class DatabaseLoadTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly DateTimeOffset AsOf = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Run_configured_database_workload()
    {
        var configPath = Environment.GetEnvironmentVariable("AE_DATABASE_LOAD_CONFIG");
        if (string.IsNullOrEmpty(configPath)) return;
        var config = JsonSerializer.Deserialize<LoadConfig>(await File.ReadAllTextAsync(configPath), Json)!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.ReportFile))!);
        await using var fixture = config.Prepare
            ? await DatabaseLoadFixture.CreateAsync(config.FixtureDirectory, config.Scale, config.Dimension)
            : await DatabaseLoadFixture.OpenAsync(config.FixtureDirectory, config.Scale, config.Dimension);
        if (config.Prepare)
        {
            await File.WriteAllTextAsync(config.ReportFile, JsonSerializer.Serialize(new { fixture.Counts, config.Scale, config.Dimension }, Json));
            return;
        }
        var probe = new CommandProbe();
        await using var seedContext = await fixture.Factory.CreateDbContextAsync();
        var factory = new MeasuredFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
            .UseSqlite(seedContext.Database.GetConnectionString()).AddInterceptors(probe).Options);
        var activity = new ActivityReadService(factory);
        var imports = new ImportHistoryService(factory);
        var routes = new RouteReadService(factory);
        var segments = new SegmentReadService(factory);
        var statistics = new StatisticsReadService(factory);
        var exploration = new ExplorationQueryService(factory);
        var map = new MapFeatureService(factory);
        var owner = fixture.OwnerId;
        var allTime = new ReportingDateSelection(ReportingPreset.AllTime);
        var fullPeriod = new ReportingDateSelection(ReportingPreset.Custom, new(2020, 1, 1), new(2026, 12, 31));
        var ownerCount = await seedContext.Activities.CountAsync(x => x.OwnerId == owner);
        // Real public services include filtering, snapshots, materialization, and result shaping.
        var scenarios = new List<Scenario>
        {
            Make("activities-owner", ct => activity.SearchAsync(new(OwnerId: owner, AsOfUtc: AsOf), ct), r => Assert.Equal(ownerCount, r.Total)),
            Make("activities-all", ct => activity.SearchAsync(new(AsOfUtc: AsOf), ct), r => Assert.Equal(fixture.Counts["Activities"], r.Total)),
            Make("activities-deep-page", ct => activity.SearchAsync(new(OwnerId: owner, Page: Math.Max(1, (ownerCount - 1) / 25), Sort: "distance-desc", AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Items)),
            Make("activities-text", ct => activity.SearchAsync(new(OwnerId: owner, Search: "Hill", AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Items)),
            Make("activities-metric", ct => activity.SearchAsync(new(OwnerId: owner, Criteria: new(MinDistanceMeters: 5925), AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Items)),
            Make("activities-tag", ct => activity.SearchAsync(new(OwnerId: owner, TagIds: [fixture.TagId], AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Items)),
            Make("activities-import", ct => activity.SearchAsync(new(OwnerId: owner, ImportBatchId: fixture.ImportBatchId, AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Items)),
            Make("activity-detail", ct => activity.GetDetailAsync(owner, fixture.ActivityId, ct), r => Assert.NotNull(r)),
            Make("filter-options", ct => activity.GetFilterOptionsAsync(owner, ct)),
            Make("sport-counts", ct => activity.GetSportActivityCountsAsync(owner, ct), r => Assert.Equal(ownerCount, r.Values.Sum())),
            Make("imports-owner", ct => imports.ListAsync(owner, 10, ct), r => Assert.NotEmpty(r.Items)),
            Make("imports-all", ct => imports.ListAsync(null, 10, ct), r => Assert.NotEmpty(r.Items)),
            Make("routes-library", ct => routes.SearchAsync(new(OwnerId: owner), new ReadPage(), ct), r => Assert.NotEmpty(r.Items)),
            Make("segments-library", ct => segments.SearchAsync(owner, null, null, "name-asc", new(), ct), r => Assert.NotEmpty(r.Items)),
            Make("segment-efforts", ct => segments.GetEffortsAsync(owner, fixture.SegmentId, new(), new(), ct), r => Assert.NotNull(r)),
            Make("dashboard", ct => activity.GetDashboardAsync(new(OwnerId: owner, Period: allTime, AsOfUtc: AsOf), ct), r => Assert.Equal(ownerCount, r.Totals.ActivityCount)),
            Make("calendar", ct => activity.GetTrainingCalendarAsync(new(OwnerId: owner, Month: new(2020, 1, 1), AsOfUtc: AsOf), ct), r => Assert.True(r.Totals.ActivityCount > 0)),
            Make("records-persisted", ct => statistics.GetRecordsAsync(owner, cancellationToken: ct), r => Assert.NotEmpty(r)),
            Make("records-attempts", ct => statistics.GetAttemptsAsync(new(SportKind.Running, RecordKind.Distance, "Longest distance", owner, AsOfUtc: AsOf), ct), r => Assert.NotNull(r)),
            Make("records-history", ct => statistics.GetHistoryAsync(new(SportKind.Running, RecordKind.Distance, "Longest distance", owner, AsOfUtc: AsOf), ct), r => Assert.NotNull(r)),
            Make("records-period-streams", ct => statistics.GetOverviewAsync(new(OwnerId: owner, Sport: SportKind.Running, Period: fullPeriod, AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Records)),
            Make("exploration", ct => exploration.GetAsync(new(new(owner), allTime, AsOf), ct), r => { Assert.True(r.Index.IsComplete); Assert.NotNull(r.Summary); Assert.True(r.Summary.VisitedCells > 0); }),
            Make("map-activities", ct => map.GetActivitiesAsync(new(OwnerId: owner, AsOfUtc: AsOf), ct), r => Assert.NotEmpty(r.Features))
        };
        if (config.Scenarios is { Length: > 0 }) scenarios = scenarios.Where(x => config.Scenarios.Contains(x.Name, StringComparer.Ordinal)).ToList();
        Assert.NotEmpty(scenarios);
        var samples = new List<object>();
        var random = new Random(7219 + config.Block);
        foreach (var scenario in scenarios.OrderBy(_ => random.Next()))
        {
            for (var warmup = 0; warmup < config.Warmups; warmup++)
                _ = await MeasureAsync(scenario, warmup: true);
            for (var repetition = 0; repetition < config.Repetitions; repetition++)
            {
                samples.Add(await MeasureAsync(scenario, warmup: false));
                await SaveAsync(); // Keep completed evidence if a later scenario crashes.
            }
        }
        await SaveAsync();

        async Task SaveAsync() => await File.WriteAllTextAsync(config.ReportFile, JsonSerializer.Serialize(new
        {
            metadata = new
            {
                config,
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                processors = Environment.ProcessorCount,
                productionModule = typeof(ActivityReadService).Assembly.ManifestModule.ModuleVersionId,
                sqlite = seedContext.Database.GetDbConnection().ServerVersion,
                databaseBytes = new FileInfo(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(seedContext.Database.GetConnectionString()).DataSource).Length,
                counts = fixture.Counts,
                measuredAtUtc = DateTimeOffset.UtcNow,
                cache = "pooled SQLite connections and warm OS cache; warmups excluded",
                commands = probe.Captured
            },
            samples
        }, Json));

        async Task<object> MeasureAsync(Scenario scenario, bool warmup)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(config.BudgetMs));
            probe.Reset(config.Diagnostic && !warmup);
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var status = "ok";
            string? error = null;
            string? fingerprint = null;
            try
            {
                var result = await scenario.Run(timeout.Token);
                var elapsedBeforeValidation = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
                scenario.Validate(result);
                // Fingerprinting and assertions are outside timed/allocated measurements.
                fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, Json))));
                return new
                {
                    config.Variant,
                    config.Dimension,
                    config.Scale,
                    config.Block,
                    scenario = scenario.Name,
                    elapsedMs = elapsedBeforeValidation,
                    allocatedBytes = allocated,
                    commands = probe.Count,
                    status = elapsedBeforeValidation > config.BudgetMs ? "budget-exceeded" : status,
                    error,
                    resultFingerprint = fingerprint,
                    config.BudgetMs
                };
            }
            catch (OperationCanceledException) { status = "timeout"; }
            catch (Exception ex) { status = "error"; error = ex.GetType().Name + ": " + ex.Message; }
            return new
            {
                config.Variant,
                config.Dimension,
                config.Scale,
                config.Block,
                scenario = scenario.Name,
                elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
                commands = probe.Count,
                status,
                error,
                resultFingerprint = fingerprint,
                config.BudgetMs
            };
        }
    }

    private static Scenario Make<T>(string name, Func<CancellationToken, Task<T>> run, Action<T>? validate = null) =>
        new(name, async ct => await run(ct), value => validate?.Invoke((T)value!));

    private sealed record Scenario(string Name, Func<CancellationToken, Task<object?>> Run, Action<object?> Validate);
    private sealed record LoadConfig(string FixtureDirectory, string ReportFile, int Scale = 1, string Dimension = "history",
        string Variant = "baseline", int Block = 0, int Warmups = 2, int Repetitions = 1, int BudgetMs = 5000,
        bool Prepare = false, bool Diagnostic = false, string[]? Scenarios = null);

    private sealed class MeasuredFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        public int Count { get; private set; }
        public List<object> Captured { get; } = [];
        private bool capture;
        private readonly HashSet<string> seen = [];
        public void Reset(bool diagnostic) { Count = 0; capture = diagnostic; }
        private void Observe(DbCommand command)
        {
            Count++;
            if (!capture || !seen.Add(command.CommandText)) return;
            var plan = new List<string>();
            using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN QUERY PLAN " + command.CommandText;
            foreach (DbParameter p in command.Parameters)
            {
                var copy = explain.CreateParameter(); copy.ParameterName = p.ParameterName; copy.Value = p.Value; copy.DbType = p.DbType;
                explain.Parameters.Add(copy);
            }
            using var reader = explain.ExecuteReader();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Captured.Add(new { sql = command.CommandText, parameters = command.Parameters.Cast<DbParameter>().Select(p => new { p.ParameterName, type = p.DbType.ToString(), value = Convert.ToString(p.Value, CultureInfo.InvariantCulture) }), plan });
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Observe(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Observe(command); return ValueTask.FromResult(result); }
    }
}

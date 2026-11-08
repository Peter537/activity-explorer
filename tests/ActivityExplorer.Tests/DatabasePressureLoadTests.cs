using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ActivityExplorer.Tests;

// Separate-process opt-in: target fixture metadata is prepared in a different invocation.
public sealed class DatabasePressureLoadTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly DateTimeOffset AsOf = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] DefaultScenarios = ["records-attempts", "records-history", "records-period-streams", "imports-owner", "imports-all", "mixed"];
    private static readonly string[] SupportedScenarios = ["activities-owner", "activities-all", "activities-deep-page", "activities-text", "activities-metric", "activities-tag", "activities-import", "activity-detail", "filter-options", "sport-counts", "imports-owner", "imports-all", "routes-library", "segments-library", "segment-efforts", "dashboard", "calendar", "records-persisted", "records-attempts", "records-history", "records-period-streams", "exploration", "map-activities", "mixed"];
    private static readonly string[] DefaultRegimes = ["cold-solo", "warm-burst", "cold-burst"];

    [Fact]
    public async Task Run_configured_pressure_workload()
    {
        var configPath = Environment.GetEnvironmentVariable("AE_DATABASE_PRESSURE_CONFIG");
        if (string.IsNullOrEmpty(configPath)) return;
        var config = JsonSerializer.Deserialize<PressureConfig>(await File.ReadAllTextAsync(configPath), Json)!;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pressure fixture copying requires Windows CopyFileExW.");
        ArgumentOutOfRangeException.ThrowIfLessThan(config.BudgetMs, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(config.Warmups);
        if (!Enum.TryParse<SqliteCacheMode>(config.CacheMode, ignoreCase: true, out var cacheMode) || !Enum.IsDefined(cacheMode))
            throw new ArgumentException("Choose Default, Private, or Shared SQLite cache mode.");
        if (config.MmapBytes is < 0) throw new ArgumentOutOfRangeException(nameof(config), "MmapBytes must be nonnegative.");
        var source = Path.Combine(config.FixtureDirectory, $"load-{config.Dimension}-{config.Scale}.db");
        var runtimeSource = Path.Combine(config.RuntimeFixtureDirectory, $"load-{config.Dimension}-1.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.ReportFile))!);
        if (config.SetupOnly)
        {
            // Reject a live/uncheckpointed source rather than copy an incomplete database.
            CheckNoWal(source);
            if (config.PrimeClone) await PrimeFixture(source, config.FixtureDirectory, config.Scale, config.Dimension);
            if (config.UpgradeReadIndexes) await UpgradeReadIndexesOnClone(source, config.PrimeClone);
            (DerivedTableEvidence? records, ActivityProjectionEvidence? periodRows) = config.PrepareRecordEffortSummaries
                ? await PrepareRecordEffortSummariesOnClone(source, config.PrimeClone) : (null, null);
            (ExplorationCellSetEvidence? cells, ActivityProjectionEvidence? aggregates) = config.PrepareExplorationCellSets
                ? await PrepareExplorationCellSetsOnClone(source, config.PrimeClone) : (null, null);
            await using var fixture = await DatabaseLoadFixture.OpenAsync(config.FixtureDirectory, config.Scale, config.Dimension);
            await using var db = await fixture.Factory.CreateDbContextAsync();
            var ownerId = config.PrimeClone ? PrimeGuid(fixture.OwnerId) : fixture.OwnerId;
            var ownerCount = await db.Activities.CountAsync(activity => activity.OwnerId == ownerId);
            var originalCounts = fixture.Counts.Where(entry => entry.Key != "ActivityRecordEffortSummaries")
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            var derivedTables = records is null && cells is null ? null : new DerivedTablesEvidence(records, cells, aggregates, periodRows);
            var metadata = new FixtureMetadata(config.Scale, config.Dimension, ownerId, ownerCount, originalCounts,
                new FileInfo(source).Length, FileHash(source), Path.GetFullPath(source), config.PrimeClone, derivedTables);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(config.MetadataFile))!);
            await File.WriteAllTextAsync(config.MetadataFile, JsonSerializer.Serialize(metadata, Json));
            return;
        }
        if (config.PrimeClone || config.UpgradeReadIndexes || config.PrepareRecordEffortSummaries || config.PrepareExplorationCellSets)
            throw new InvalidOperationException("Fixture modifications are only valid during setup.");
        var meta = JsonSerializer.Deserialize<FixtureMetadata>(await File.ReadAllTextAsync(config.MetadataFile), Json)!;
        if (meta.Scale != config.Scale || meta.Dimension != config.Dimension || meta.DatabaseBytes != new FileInfo(source).Length)
            throw new InvalidOperationException("Fixture metadata does not match source database.");
        CheckNoWal(source);
        if (meta.Primed) throw new InvalidOperationException("A priming fixture cannot be the measured target.");
        var runtimeFixtures = new List<FixtureMetadata>();
        if (config.RuntimeMetadataFiles is { Length: > 0 })
        {
            foreach (var metadataFile in config.RuntimeMetadataFiles)
            {
                var runtime = JsonSerializer.Deserialize<FixtureMetadata>(await File.ReadAllTextAsync(metadataFile), Json)!;
                if (!runtime.Primed || string.IsNullOrEmpty(runtime.SourcePath)) throw new InvalidOperationException("Runtime metadata must identify a primed fixture.");
                EnsureRuntimePath(runtime.SourcePath);
                CheckNoWal(runtime.SourcePath);
                if (runtime.OwnerId == meta.OwnerId || runtime.SourceSha256 == meta.SourceSha256 ||
                    new FileInfo(runtime.SourcePath).Length != runtime.DatabaseBytes ||
                    !string.Equals(FileHash(runtime.SourcePath), runtime.SourceSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Runtime fixture is not an independently primed verified clone.");
                if (string.Equals(Path.GetFullPath(runtime.SourcePath), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) ||
                    runtimeFixtures.Any(value => string.Equals(value.SourcePath, runtime.SourcePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Runtime fixtures must have distinct physical paths from target and each other.");
                runtimeFixtures.Add(runtime);
            }
        }
        else CheckNoWal(runtimeSource);
        var scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.ReportFile))!, "pressure-scratch");
        Directory.CreateDirectory(scratch);
        var productionHash = FileHash(typeof(StatisticsReadService).Assembly.Location);
        var driverHash = FileHash(typeof(DatabasePressureLoadTests).Assembly.Location);
        var samples = new List<object>();
        var copyChecks = new List<object>();
        var priming = new List<object>();
        var hostReadiness = new List<PressureHostGate.WaitResult>();
        var scenarios = config.Scenarios is { Length: > 0 } ? config.Scenarios : DefaultScenarios;
        var regimes = config.Regimes is { Length: > 0 } ? config.Regimes : DefaultRegimes;
        if (config.HostGate is not null && regimes.Contains("warm-burst", StringComparer.Ordinal))
            throw new ArgumentException("Host readiness gating requires cold regimes.", nameof(config));
        var jobs = new List<PressureJob>();
        foreach (var regime in regimes)
        {
            if (!DefaultRegimes.Contains(regime, StringComparer.Ordinal)) throw new ArgumentException($"Unknown regime {regime}.");
            foreach (var scenario in scenarios)
            {
                if (!SupportedScenarios.Contains(scenario, StringComparer.Ordinal)) throw new ArgumentException($"Unknown scenario {scenario}.");
                if (scenario == "mixed" && regime == "cold-solo") continue;
                var slotNames = scenario == "mixed" ? new[] { "records-period-streams", "records-attempts", "records-history", "imports-all" } : [scenario];
                var slots = regime == "cold-solo" ? slotNames.Take(1).ToArray() :
                    scenario == "mixed" ? slotNames : Enumerable.Repeat(scenario, 4).ToArray();
                var ownsTarget = regime != "warm-burst";
                var target = ownsTarget ? Path.Combine(scratch, $"{scenario}-{regime}-{Guid.NewGuid():N}.db") : source;
                jobs.Add(new(regime, scenario, slotNames, slots, target, ownsTarget));
            }
        }
        var coldJobs = jobs.Where(job => job.OwnsTarget).ToArray();
        var copyConcurrency = Math.Min(2, coldJobs.Length);
        var requiredBytes = checked(meta.DatabaseBytes * coldJobs.LongLength);
        var volume = Path.GetPathRoot(Path.GetFullPath(scratch))!;
        var freeBytes = new DriveInfo(volume).AvailableFreeSpace;
        if (requiredBytes > freeBytes) throw new IOException($"Cold copies require {requiredBytes} bytes but only {freeBytes} bytes are available.");
        using var hostGate = config.HostGate is { } policy ? new PressureHostGate(policy, volume.TrimEnd(Path.DirectorySeparatorChar)) : null;
        var created = new ConcurrentBag<string>();
        var completed = new List<PressureJob>();
        DateTimeOffset? copiesReadyUtc = null;
        DateTimeOffset? collectionReadyUtc = null;
        try
        {
            if (coldJobs.Length > 0)
            {
                await Parallel.ForEachAsync(coldJobs, new ParallelOptions { MaxDegreeOfParallelism = copyConcurrency }, (job, _) =>
                {
                    CopyCold(source, job.Target);
                    created.Add(job.Target);
                    return ValueTask.CompletedTask;
                });
                copiesReadyUtc = DateTimeOffset.UtcNow;
                await Task.Delay(TimeSpan.FromSeconds(5));
                collectionReadyUtc = DateTimeOffset.UtcNow;
            }
            foreach (var job in jobs)
            {
                if (runtimeFixtures.Count > 0)
                {
                    // Prime the four-slot shape and large-path JIT on independent, remapped fixtures.
                    const SqliteCacheMode primerCache = SqliteCacheMode.Private;
                    var primeSlots = job.Scenario == "mixed" ? job.SlotNames : Enumerable.Repeat(job.Scenario, 4).ToArray();
                    foreach (var runtime in runtimeFixtures)
                    {
                        // Prime a separate fixture before any connection to the measured cold target.
                        var primerConnection = new SqliteConnectionStringBuilder
                        {
                            DataSource = runtime.SourcePath!,
                            Cache = primerCache,
                            Pooling = false,
                            DefaultTimeout = 30
                        };
                        await using var anchor = new SqliteConnection(primerConnection.ToString());
                        await anchor.OpenAsync();
                        await using var schema = anchor.CreateCommand();
                        schema.CommandText = "PRAGMA schema_version";
                        await schema.ExecuteScalarAsync();
                        for (var i = 0; i < 2; i++)
                        {
                            var started = DateTimeOffset.UtcNow;
                            var wave = await Wave(primeSlots, runtime.SourcePath!, runtime, 60_000, primerCache, config.MmapBytes, pooled: true);
                            RequireSuccessfulWave(wave);
                            priming.Add(new
                            {
                                scenario = job.Scenario,
                                regime = job.Regime,
                                sourcePath = runtime.SourcePath,
                                runtime.Scale,
                                iteration = i,
                                concurrency = primeSlots.Length,
                                budgetMs = 60_000,
                                sqliteCacheMode = primerCache.ToString(),
                                startedUtc = started,
                                completedUtc = DateTimeOffset.UtcNow
                            });
                        }
                    }
                    SqliteConnection.ClearAllPools();
                }
                else
                {
                    // Legacy tiny-only runtime warmup retained for controlled comparisons.
                    for (var i = 0; i < 2; i++)
                        foreach (var name in job.SlotNames.Distinct(StringComparer.Ordinal))
                            await Run(name, NewFactory(runtimeSource, new(), cacheMode, config.MmapBytes), meta.OwnerId, CancellationToken.None);
                }
                if (job.Regime == "warm-burst")
                    for (var i = 0; i < config.Warmups; i++)
                        RequireSuccessfulWave(await Wave(job.Slots, job.Target, meta, 60_000, cacheMode, config.MmapBytes, pooled: true));
                samples.Add(await Wave(job.Slots, job.Target, meta, config.BudgetMs, cacheMode, config.MmapBytes, config.Variant,
                    config.Dimension, config.Scale, config.Block, job.Scenario + "." + job.Regime, job.Scenario, job.Regime,
                    job.Regime == "warm-burst", config.Diagnostic, hostGate is null ? null : WaitForHost));
                completed.Add(job);
                await Save(); // Persist each wave before beginning the next one.

                async Task WaitForHost()
                {
                    var observation = await hostGate!.WaitAsync(job.Scenario, job.Regime);
                    hostReadiness.Add(observation);
                    if (!observation.Passed)
                        throw new InvalidOperationException($"The host did not reach the pre-arrival limits for {job.Scenario}.");
                }
            }

            // All timed requests have drained before inspecting target files or hashing them.
            SqliteConnection.ClearAllPools();
            string? warmHash = null;
            DateTimeOffset? warmHashUtc = null;
            foreach (var job in completed)
            {
                CheckNoWal(job.Target);
                var copiedHash = job.OwnsTarget ? FileHash(job.Target) : (warmHash ??= FileHash(job.Target));
                var verifiedUtc = job.OwnsTarget ? DateTimeOffset.UtcNow : (warmHashUtc ??= DateTimeOffset.UtcNow);
                var copyMatches = string.Equals(copiedHash, meta.SourceSha256, StringComparison.Ordinal);
                copyChecks.Add(new
                {
                    scenario = job.Scenario,
                    regime = job.Regime,
                    targetKind = job.OwnsTarget ? "unbuffered-copy" : "validated-warm-source",
                    copiedHash,
                    verifiedUtc,
                    matches = copyMatches
                });
                await Save();
                if (!copyMatches) throw new InvalidOperationException("Timed fixture differs from its preflight source hash.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { await Save(); }
            finally
            {
                foreach (var target in created)
                    foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(target + suffix);
            }
        }

        async Task Save()
        {
            var stagedReport = config.ReportFile + ".tmp";
            await using (var report = File.Create(stagedReport))
            {
                await JsonSerializer.SerializeAsync(report, new
                {
                    metadata = new
                    {
                        config,
                        fixture = meta,
                        runtime = RuntimeInformation.FrameworkDescription,
                        os = RuntimeInformation.OSDescription,
                        processors = Environment.ProcessorCount,
                        serverGc = System.Runtime.GCSettings.IsServerGC,
                        productionModule = typeof(StatisticsReadService).Assembly.ManifestModule.ModuleVersionId,
                        productionHash,
                        driverHash,
                        cache = "Cold regimes: unbuffered-file-first-touch on a fresh nonpooled SQLite connection; warm bursts use the validated source with pooled connections and target warmups. CopyFileExW NO_BUFFERING is used; background Windows and device cache state is uncontrolled, and no global cache flush is performed. Runtime warmup uses separate fixture(s) as recorded in priming metadata; an empty list indicates the legacy tiny-only path.",
                        sqliteCacheMode = cacheMode.ToString(),
                        requestedMmapLimitBytes = config.MmapBytes,
                        mmapNote = "PRAGMA mmap_size is a configured limit, not resident or mapped bytes.",
                        copyFlags = "COPY_FILE_NO_BUFFERING | COPY_FILE_FAIL_IF_EXISTS",
                        coldPreparation = new
                        {
                            replicaCount = coldJobs.Length,
                            copyConcurrency,
                            requiredBytes,
                            freeBytes,
                            settleMs = coldJobs.Length > 0 ? 5000 : 0,
                            copiesReadyUtc,
                            collectionReadyUtc
                        },
                        priming,
                        hostReadiness,
                        copyChecks
                    },
                    samples
                }, Json);
            }
            File.Move(stagedReport, config.ReportFile, overwrite: true);
        }
    }

    private static async Task<object> Wave(string[] names, string path, FixtureMetadata meta, int budgetMs,
        SqliteCacheMode cacheMode, long? mmapBytes, string variant = "warmup", string dimension = "", int scale = 0, int block = 0, string scenario = "warmup", string baseScenario = "warmup", string regime = "warmup", bool pooled = false, bool diagnostic = false,
        Func<Task>? beforeArrival = null)
    {
        var gate = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var deadlines = names.Select(_ => new CancellationTokenSource()).ToArray();
        var probes = names.Select(_ => new RequestProbe(diagnostic)).ToArray();
        var tasks = names.Select((name, slot) => Task.Run(async () =>
        {
            var factory = NewFactory(path, probes[slot], cacheMode, mmapBytes, pooled);
            if (Interlocked.Increment(ref readyCount) == names.Length) ready.TrySetResult();
            var start = await gate.Task;
            var entered = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            try
            {
                var result = await Run(name, factory, meta.OwnerId, deadlines[slot].Token, meta.OwnerCount, meta.Primed);
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                return new RequestResult(slot, name, elapsed, entered, elapsed < budgetMs ? "ok" : "budget-exceeded", null, result, probes[slot].Count, probes[slot].MmapLimits.ToArray());
            }
            catch (OperationCanceledException) { return new RequestResult(slot, name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, entered, "timeout", null, null, probes[slot].Count, probes[slot].MmapLimits.ToArray()); }
            catch (Exception error) { return new RequestResult(slot, name, Stopwatch.GetElapsedTime(start).TotalMilliseconds, entered, "error", error.GetType().Name + ": " + error.Message, null, probes[slot].Count, probes[slot].MmapLimits.ToArray()); }
        })).ToArray();
        await ready.Task;
        if (beforeArrival is not null)
        {
            try { await beforeArrival(); }
            catch
            {
                gate.SetCanceled();
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) { }
                foreach (var deadline in deadlines) deadline.Dispose();
                throw;
            }
        }
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var arrivalUtc = DateTimeOffset.UtcNow;
        var arrival = Stopwatch.GetTimestamp();
        foreach (var deadline in deadlines) deadline.CancelAfter(budgetMs);
        gate.SetResult(arrival);
        var results = await Task.WhenAll(tasks);
        var allocatedAfter = GC.GetTotalAllocatedBytes(true);
        long? bytes = allocatedAfter >= allocatedBefore ? allocatedAfter - allocatedBefore : null;
        var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;
        foreach (var deadline in deadlines) deadline.Dispose();
        var requestReports = results.Select(result =>
        {
            var valid = result.Status != "ok" || Valid(result.Name, result.Result, meta);
            var status = valid ? result.Status : "invalid-result";
            var fingerprint = status == "ok" ? Hash(JsonSerializer.Serialize(result.Result, Json)) : null;
            return new { result.Slot, scenario = result.Name, elapsedMs = result.Elapsed, enteredMs = result.Entered, status, result.Error, result.Commands, effectiveMmapLimitBytes = result.MmapLimits, diagnostics = probes[result.Slot].ReaderTimings?.ToArray(), resultFingerprint = fingerprint };
        }).ToArray();
        var status = requestReports.All(r => r.status == "ok") ? "ok" : requestReports.First(r => r.status != "ok").status;
        var composite = status == "ok" ? Hash(string.Join("\n", requestReports.Select(r => $"{r.Slot}|{r.scenario}|{r.resultFingerprint}"))) : null;
        return new
        {
            variant,
            dimension,
            scale,
            block,
            scenario,
            baseScenario,
            regime,
            concurrency = names.Length,
            arrivalUtc,
            cacheMode = regime == "warm-burst" ? "warm-sqlite-os-uncontrolled" : "unbuffered-file-first-touch",
            sqliteCacheMode = cacheMode.ToString(),
            requestedMmapLimitBytes = mmapBytes,
            elapsedMs = requestReports.Max(r => r.elapsedMs),
            allocatedBytes = bytes,
            allocatedBefore,
            allocatedAfter,
            allocationNote = bytes.HasValue ? null : "Process allocation counter decreased; delta unavailable.",
            commands = requestReports.Sum(r => r.Commands),
            cpuMs,
            gen0,
            gen1,
            gen2,
            status,
            resultFingerprint = composite,
            budgetMs,
            requests = requestReports
        };
    }

    private static bool Valid(string name, object? result, FixtureMetadata meta) => name switch
    {
        "activities-owner" => result is ActivitySearchResult owner && owner.Total == meta.OwnerCount,
        "activities-all" => result is ActivitySearchResult all && all.Total == meta.Counts["Activities"],
        "activities-deep-page" or "activities-text" or "activities-metric" or "activities-tag" or "activities-import" =>
            result is ActivitySearchResult filtered && filtered.Items.Count > 0,
        "activity-detail" => result is ActivityReadDetail detail && detail.Summary.Id == FixtureId("activity", meta.Primed),
        "filter-options" => result is ActivityFilterOptions options && options.Devices.Count > 0,
        "sport-counts" => result is IReadOnlyDictionary<SportKind, int> counts && counts.Values.Sum() == meta.OwnerCount,
        "routes-library" => result is PagedResult<RouteSummary> routes && routes.Items.Count > 0,
        "segments-library" => result is PagedResult<SegmentSummary> segments && segments.Items.Count > 0,
        "segment-efforts" => result is PagedResult<SegmentEffortSummary> efforts && efforts.Items.Count > 0,
        "dashboard" => result is DashboardSummary dashboard && dashboard.Totals.ActivityCount == meta.OwnerCount,
        "calendar" => result is TrainingCalendarSummary calendar && calendar.Totals.ActivityCount > 0,
        "records-persisted" => result is IReadOnlyList<PersonalRecord> records && records.Count > 0,
        "records-attempts" => result is RecordAttemptPage page && page.Attempts.Total > 0,
        "records-history" => result is RecordHistory history && history.Series.Count > 0,
        "records-period-streams" => result is RecordOverviewResult overview && overview.Records.Count > 0,
        "imports-owner" or "imports-all" => result is ImportHistoryPage imports && imports.Items.Count > 0,
        "exploration" => result is ExplorationResult exploration && exploration.Index.IsComplete && exploration.Summary is { VisitedCells: > 0 },
        "map-activities" => result is MapFeatureCollection map && map.Features.Count > 0,
        _ => false
    };

    private static async Task<object?> Run(string name, IDbContextFactory<ExplorerDbContext> factory, Guid owner, CancellationToken ct, int ownerCount = 50, bool primeIds = false)
    {
        var stats = new StatisticsReadService(factory);
        var imports = new ImportHistoryService(factory);
        var activities = new ActivityReadService(factory);
        var routes = new RouteReadService(factory);
        var segments = new SegmentReadService(factory);
        var exploration = new ExplorationQueryService(factory);
        var map = new MapFeatureService(factory);
        var tag = FixtureId("tag", primeIds);
        var batch = FixtureId("batch", primeIds);
        var activityId = FixtureId("activity", primeIds);
        var segmentId = FixtureId("segment", primeIds);
        var allTime = new ReportingDateSelection(ReportingPreset.AllTime);
        var period = new ReportingDateSelection(ReportingPreset.Custom, new(2020, 1, 1), new(2026, 12, 31));
        return name switch
        {
            "activities-owner" => await activities.SearchAsync(new(OwnerId: owner, AsOfUtc: AsOf), ct),
            "activities-all" => await activities.SearchAsync(new(AsOfUtc: AsOf), ct),
            "activities-deep-page" => await activities.SearchAsync(new(OwnerId: owner, Page: Math.Max(1, (ownerCount - 1) / 25), Sort: "distance-desc", AsOfUtc: AsOf), ct),
            "activities-text" => await activities.SearchAsync(new(OwnerId: owner, Search: "Hill", AsOfUtc: AsOf), ct),
            "activities-metric" => await activities.SearchAsync(new(OwnerId: owner, Criteria: new(MinDistanceMeters: 5925), AsOfUtc: AsOf), ct),
            "activities-tag" => await activities.SearchAsync(new(OwnerId: owner, TagIds: [tag], AsOfUtc: AsOf), ct),
            "activities-import" => await activities.SearchAsync(new(OwnerId: owner, ImportBatchId: batch, AsOfUtc: AsOf), ct),
            "activity-detail" => await activities.GetDetailAsync(owner, activityId, ct),
            "filter-options" => await activities.GetFilterOptionsAsync(owner, ct),
            "sport-counts" => await activities.GetSportActivityCountsAsync(owner, ct),
            "routes-library" => await routes.SearchAsync(new(OwnerId: owner), new ReadPage(), ct),
            "segments-library" => await segments.SearchAsync(owner, null, null, "name-asc", new(), ct),
            "segment-efforts" => await segments.GetEffortsAsync(owner, segmentId, new(), new(), ct),
            "dashboard" => await activities.GetDashboardAsync(new(OwnerId: owner, Period: allTime, AsOfUtc: AsOf), ct),
            "calendar" => await activities.GetTrainingCalendarAsync(new(OwnerId: owner, Month: new(2020, 1, 1), AsOfUtc: AsOf), ct),
            "records-persisted" => await stats.GetRecordsAsync(owner, cancellationToken: ct),
            "exploration" => await exploration.GetAsync(new(new(owner), allTime, AsOf), ct),
            "map-activities" => await map.GetActivitiesAsync(new(OwnerId: owner, AsOfUtc: AsOf), ct),
            "records-attempts" => await stats.GetAttemptsAsync(new(SportKind.Running, RecordKind.Distance, "Longest distance", owner, AsOfUtc: AsOf), ct),
            "records-history" => await stats.GetHistoryAsync(new(SportKind.Running, RecordKind.Distance, "Longest distance", owner, AsOfUtc: AsOf), ct),
            "records-period-streams" => await stats.GetOverviewAsync(new(OwnerId: owner, Sport: SportKind.Running, Period: period, AsOfUtc: AsOf), ct),
            "imports-owner" => await imports.ListAsync(owner, 10, ct),
            "imports-all" => await imports.ListAsync(null, 10, ct),
            _ => throw new ArgumentException("Unknown pressure scenario.", nameof(name))
        };
    }

    private static Factory NewFactory(string path, RequestProbe probe, SqliteCacheMode cacheMode, long? mmapBytes, bool pooled = false)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 30, Pooling = pooled, Cache = cacheMode }.ToString();
        return new(new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(cs).AddInterceptors(probe).Options, mmapBytes);
    }
    private sealed class Factory(DbContextOptions<ExplorerDbContext> options, long? mmapBytes) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext()
        {
            var context = new ExplorerDbContext(options);
            if (mmapBytes is not long requested) return context;
            var connection = context.Database.GetDbConnection();
            connection.StateChange += (_, change) =>
            {
                if (change.CurrentState != ConnectionState.Open) return;
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA main.mmap_size = {requested.ToString(CultureInfo.InvariantCulture)}";
                command.ExecuteNonQuery();
            };
            return context;
        }
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class RequestProbe(bool diagnostic = false) : DbCommandInterceptor
    {
        private int count;
        public int Count => count;
        public ConcurrentQueue<long> MmapLimits { get; } = new();
        public ConcurrentQueue<ReaderTiming>? ReaderTimings { get; } = diagnostic ? new() : null;
        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            ReaderTimings?.Enqueue(new(command.CommandText, eventData.ReadCount, eventData.StartTime,
                eventData.StartTime + eventData.Duration, eventData.Duration.TotalMilliseconds));
            return result;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            ObserveReader(command);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ObserveReader(command);
            return ValueTask.FromResult(result);
        }
        private void ObserveReader(DbCommand command)
        {
            if (Interlocked.Increment(ref count) != 1) return;

            using var pragma = command.Connection!.CreateCommand();
            pragma.Transaction = command.Transaction;
            pragma.CommandText = "PRAGMA main.mmap_size";
            if (pragma.ExecuteScalar() is not long limit)
                throw new InvalidOperationException("SQLite did not report its mmap limit.");
            MmapLimits.Enqueue(limit);
        }
    }
    private sealed record FixtureMetadata(int Scale, string Dimension, Guid OwnerId, int OwnerCount, IReadOnlyDictionary<string, long> Counts, long DatabaseBytes, string SourceSha256, string? SourcePath = null, bool Primed = false, DerivedTablesEvidence? DerivedTables = null);
    private sealed record PressureConfig(string FixtureDirectory, string MetadataFile, string RuntimeFixtureDirectory, string ReportFile, int Scale = 100,
        string Dimension = "history", string Variant = "baseline", int Block = 0, int BudgetMs = 2000, int Warmups = 2,
        bool SetupOnly = false, string[]? Regimes = null, string[]? Scenarios = null, bool UpgradeReadIndexes = false, bool PrepareRecordEffortSummaries = false,
        string CacheMode = "Shared", long? MmapBytes = null, string[]? RuntimeMetadataFiles = null, bool PrimeClone = false,
        bool Diagnostic = false, bool PrepareExplorationCellSets = false, PressureHostGate.Settings? HostGate = null);
    private sealed record PressureJob(string Regime, string Scenario, string[] SlotNames, string[] Slots, string Target, bool OwnsTarget);
    private sealed record ReaderTiming(string Sql, int ReadCount, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double DurationMs);
    private sealed record DerivedTableEvidence(long RowCount, long SourceStreamCount, long InvalidRows, int ComputationVersion);
    private sealed record ExplorationCellSetEvidence(long RowCount, long SourceIndexCount, long InvalidRows, int ComputationVersion);
    private sealed record ActivityProjectionEvidence(long RowCount, long SourceActivityCount, long InvalidRows, int ComputationVersion);
    private sealed record DerivedTablesEvidence(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DerivedTableEvidence? ActivityRecordEffortSummaries,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExplorationCellSetEvidence? ActivityExplorationCellSets,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActivityProjectionEvidence? ActivityExplorationAggregateRows,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActivityProjectionEvidence? ActivityPeriodRecordRows);
    private sealed record RequestResult(int Slot, string Name, double Elapsed, double Entered, string Status, string? Error, object? Result, int Commands, long[] MmapLimits);
    private static void RequireSuccessfulWave(object wave)
    {
        var value = JsonSerializer.SerializeToElement(wave, Json);
        if (value.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException("A runtime or target warmup wave did not complete successfully: " + value.GetRawText());
    }
    private static Guid FixtureId(string kind, bool primed) => primed ? PrimeGuid(Id(kind, 0, 0)) : Id(kind, 0, 0);
    private static Guid PrimeGuid(Guid id) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"runtime-prime|{id:D}"))[..16]);
    private static Guid Id(string kind, int graph, int ordinal) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"fixture:{kind}:{graph}:{ordinal}"))[..16]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string FileHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    [Fact]
    public void File_hash_can_read_database_held_by_a_pooled_sqlite_connection()
    {
        var directory = TestSupport.NewDirectory();
        var path = Path.Combine(directory, "pooled-hash.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        try
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Sample (Value INTEGER NOT NULL); INSERT INTO Sample VALUES (1)";
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            var expected = FileHash(path);
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM Sample";
                Assert.Equal(1L, command.ExecuteScalar());
            }
            Assert.Equal(expected, FileHash(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
        }
    }

    private static void EnsureRuntimePath(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = new DirectoryInfo(Path.GetDirectoryName(full)!);
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Name == "database-pressure-runtime" && current.Parent?.Name == "artifacts") return;
        }
        throw new InvalidOperationException("Priming requires an isolated clone under artifacts/database-pressure-runtime.");
    }

    private static async Task PrimeFixture(string source, string directory, int scale, string dimension)
    {
        EnsureRuntimePath(source);
        // A marker is created before mutation so interrupted priming cannot be applied twice.
        var manifest = Path.Combine(directory, $"load-{dimension}-{scale}.db.manifest");
        if (!File.Exists(manifest) || await File.ReadAllTextAsync(manifest) != $"activity-explorer-load-v1:{dimension}:{scale}")
            throw new InvalidOperationException("Runtime fixture manifest is missing or incompatible.");
        using (new FileStream(source + ".prime-marker", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        Dictionary<string, HashSet<string>> columns = new(StringComparer.Ordinal);
        var modelOptions = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite($"Data Source={source};Pooling=False").Options;
        await using (var context = new ExplorerDbContext(modelOptions))
        {
            foreach (var entity in context.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (table is null || table == "ActivityRecordEffortSummaries") continue;
                var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType != typeof(Guid) && property.ClrType != typeof(Guid?)) continue;
                    var column = property.GetColumnName(store);
                    if (column is null) continue;
                    if (!columns.TryGetValue(table, out var names)) columns.Add(table, names = new(StringComparer.Ordinal));
                    names.Add(column);
                }
            }
        }
        SqliteConnection.ClearAllPools();
        var cs = new SqliteConnectionStringBuilder { DataSource = source, ForeignKeys = true, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(cs))
        {
            connection.CreateFunction<string?, string?>("prime_guid", value => value is null ? null : PrimeGuid(Guid.Parse(value)).ToString("D").ToUpperInvariant(), isDeterministic: true);
            await connection.OpenAsync();
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "PRAGMA defer_foreign_keys = ON";
                await command.ExecuteNonQueryAsync();
                static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                foreach (var (table, names) in columns)
                {
                    command.CommandText = $"UPDATE {Quote(table)} SET {string.Join(", ", names.Select(name => $"{Quote(name)} = prime_guid({Quote(name)})"))}";
                    await command.ExecuteNonQueryAsync();
                }
                command.CommandText = "UPDATE \"Owners\" SET \"DisplayName\" = 'Runtime prime ' || \"DisplayName\"; UPDATE \"Activities\" SET \"Title\" = 'Runtime prime ' || \"Title\"";
                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await checkpoint.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        CheckNoWal(source);
    }

    private static void EnsureCandidatePath(string source)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(source))!);
        for (var current = directory; current is not null; current = current.Parent)
        {
            if (current.Name == "database-pressure-index-candidates" && current.Parent?.Name == "artifacts") return;
        }
        throw new InvalidOperationException("Read-index upgrades require a fixture clone under artifacts/database-pressure-index-candidates.");
    }

    // Kept separate so the frozen older baseline need not resolve the newer production helper.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task UpgradeReadIndexesOnClone(string source, bool primed)
    {
        if (primed) EnsureRuntimePath(source); else EnsureCandidatePath(source);
        var cs = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false, ForeignKeys = true }.ToString();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(cs).Options;
        await using (var db = new ExplorerDbContext(options))
            await DatabaseInitializer.EnsureReadIndexesAsync(db);
        await using (var connection = new SqliteConnection(cs))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        CheckNoWal(source);
    }

    // Isolate the new helper so ordinary runs against the frozen older assembly never resolve it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(DerivedTableEvidence, ActivityProjectionEvidence)> PrepareRecordEffortSummariesOnClone(string source, bool primed)
    {
        if (primed) EnsureRuntimePath(source); else EnsureCandidatePath(source);
        var cs = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false, ForeignKeys = true }.ToString();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(cs).Options;
        DerivedTableEvidence evidence;
        ActivityProjectionEvidence periodEvidence;
        await using (var db = new ExplorerDbContext(options))
        {
            await DatabaseInitializer.EnsureRecordEffortSummariesAsync(db);
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityRecordEffortSummaries\"";
            var rows = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityStreams\"";
            var streams = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = """
                SELECT COUNT(*) FROM "ActivityRecordEffortSummaries" AS s
                LEFT JOIN "ActivityStreams" AS st ON st."ActivityId" = s."ActivityId"
                LEFT JOIN "Activities" AS a ON a."Id" = s."ActivityId"
                WHERE st."ActivityId" IS NULL OR a."Id" IS NULL OR s."Sport" IS NULL OR s."Sport" <> a."Sport"
                   OR s."ComputationVersion" IS NULL OR s."ComputationVersion" <> @version
                   OR s."ValuesBlob" IS NULL OR typeof(s."ValuesBlob") <> 'blob'
                   OR length(s."ValuesBlob") < 1 OR substr(s."ValuesBlob", 1, 1) <> X'02'
                   OR (length(s."ValuesBlob") - 1) % 34 <> 0
                   OR (length(s."ValuesBlob") - 1) / 34 > @maxEntries
                """;
            var version = command.CreateParameter();
            version.ParameterName = "@version";
            version.Value = RecordEffortReadModel.ComputationVersion;
            command.Parameters.Add(version);
            var maximum = command.CreateParameter();
            maximum.ParameterName = "@maxEntries";
            maximum.Value = Enum.GetValues<SportKind>().Max(sport => RecordEffortReadModel.GroupsFor(sport).Sum(group => group.Length));
            command.Parameters.Add(maximum);
            var invalid = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            evidence = new(rows, streams, invalid, RecordEffortReadModel.ComputationVersion);
            if (rows != streams || invalid != 0) throw new InvalidOperationException("Prepared record-effort summaries are incomplete or invalid.");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityPeriodRecordRows\"";
            var periodRows = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM \"Activities\"";
            var activities = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = """
                SELECT COUNT(*) FROM "ActivityPeriodRecordRows" AS r
                LEFT JOIN "Activities" AS a ON a."Id" = r."ActivityId"
                LEFT JOIN "ActivityStreams" AS st ON st."ActivityId" = r."ActivityId"
                LEFT JOIN "ActivityRecordEffortSummaries" AS s ON s."ActivityId" = r."ActivityId"
                WHERE r."OwnerId" IS NOT a."OwnerId" OR r."Sport" IS NOT a."Sport"
                   OR r."StartTimeUtc" IS NOT a."StartTimeUtc" OR r."Title" IS NOT a."Title"
                   OR r."DistanceMeters" IS NOT a."DistanceMeters" OR r."MovingTimeSeconds" IS NOT a."MovingTimeSeconds"
                   OR r."ElevationGainMeters" IS NOT a."ElevationGainMeters" OR r."IsIndoor" IS NOT a."IsIndoor"
                   OR r."HasStream" IS NOT (st."ActivityId" IS NOT NULL)
                   OR r."SummarySport" IS NOT s."Sport" OR r."ComputationVersion" IS NOT s."ComputationVersion"
                   OR r."ValuesBlob" IS NOT s."ValuesBlob"
                """;
            var invalidPeriodRows = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            periodEvidence = new(periodRows, activities, invalidPeriodRows, PeriodRecordProjectionStore.FormatVersion);
            if (periodRows != activities || invalidPeriodRows != 0)
                throw new InvalidOperationException("Prepared period record rows differ from the source activities or summaries.");
        }
        await using (var connection = new SqliteConnection(cs))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        CheckNoWal(source);
        return (evidence, periodEvidence);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ExplorationCellSetEvidence, ActivityProjectionEvidence)> PrepareExplorationCellSetsOnClone(string source, bool primed)
    {
        if (primed) EnsureRuntimePath(source); else EnsureCandidatePath(source);
        var cs = new SqliteConnectionStringBuilder { DataSource = source, Pooling = false, ForeignKeys = true }.ToString();
        var options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(cs).Options;
        ExplorationCellSetEvidence evidence;
        ActivityProjectionEvidence aggregateEvidence;
        await using (var db = new ExplorerDbContext(options))
        {
            await ExplorationCellSetStore.EnsureAsync(db, CancellationToken.None);
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityExplorationCellSets\"";
            var rows = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityExplorationIndexes\"";
            var indexes = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = """
                SELECT COUNT(*) FROM "ActivityExplorationCellSets" AS s
                LEFT JOIN "ActivityExplorationIndexes" AS i ON i."ActivityId" = s."ActivityId"
                WHERE i."ActivityId" IS NULL OR s."FormatVersion" <> @version
                   OR s."CellsJson" <> (
                        SELECT json_group_array(c."CellId" ORDER BY c."CellId")
                        FROM "ActivityExplorationCells" AS c WHERE c."ActivityId" = s."ActivityId")
                   OR CASE WHEN json_valid(s."CellsJson") THEN json_array_length(s."CellsJson") <> i."CellCount" ELSE 1 END
                """;
            var version = command.CreateParameter();
            version.ParameterName = "@version";
            version.Value = ExplorationCellSetStore.FormatVersion;
            command.Parameters.Add(version);
            var invalid = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            evidence = new(rows, indexes, invalid, ExplorationCellSetStore.FormatVersion);
            if (rows != indexes || invalid != 0) throw new InvalidOperationException("Prepared exploration cell sets differ from the source memberships.");
            command.Parameters.Clear();
            command.CommandText = "SELECT COUNT(*) FROM \"ActivityExplorationAggregateRows\"";
            var aggregateRows = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = "SELECT COUNT(*) FROM \"Activities\"";
            var activities = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            command.CommandText = """
                SELECT COUNT(*) FROM "ActivityExplorationAggregateRows" AS r
                LEFT JOIN "Activities" AS a ON a."Id" = r."ActivityId"
                LEFT JOIN "ActivityExplorationIndexes" AS i ON i."ActivityId" = r."ActivityId"
                LEFT JOIN "ActivityExplorationCellSets" AS s ON s."ActivityId" = r."ActivityId"
                WHERE r."OwnerId" IS NOT a."OwnerId" OR r."Sport" IS NOT a."Sport"
                   OR r."StartTimeTicks" IS NOT a."StartTimeUtc"
                   OR r."ExplorationInputVersion" IS NOT a."ExplorationInputVersion"
                   OR r."InputVersion" IS NOT i."InputVersion" OR r."ComputationVersion" IS NOT i."ComputationVersion"
                   OR r."IsLimited" IS NOT i."IsLimited" OR r."CellCount" IS NOT i."CellCount"
                   OR r."CellsJson" IS NOT s."CellsJson" OR r."FormatVersion" IS NOT s."FormatVersion"
                   OR r."AggregateVersion" <> @version
                """;
            version.Value = ExplorationAggregateStore.FormatVersion;
            command.Parameters.Add(version);
            var invalidAggregates = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            aggregateEvidence = new(aggregateRows, activities, invalidAggregates, ExplorationAggregateStore.FormatVersion);
            if (aggregateRows != activities || invalidAggregates != 0)
                throw new InvalidOperationException("Prepared exploration aggregates differ from the source activities or cell sets.");
            command.Parameters.Clear();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        CheckNoWal(source);
        return (evidence, aggregateEvidence);
    }

    [Fact]
    public void Read_index_upgrade_rejects_paths_outside_candidate_clones()
    {
        var path = Path.Combine(TestSupport.NewDirectory(), "original.db");
        Assert.Throws<InvalidOperationException>(() => EnsureCandidatePath(path));
    }

    private static void CheckNoWal(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Fixture database is missing.", path);
        if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
            throw new InvalidOperationException("Fixture has a nonempty WAL; close/checkpoint it before pressure setup.");
    }
    [Fact]
    public void Cold_copy_refuses_to_replace_existing_file()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = TestSupport.NewDirectory();
        var source = Path.Combine(directory, "copy-source.db");
        var target = Path.Combine(directory, "copy-target.db");
        try
        {
            File.WriteAllText(source, "source");
            File.WriteAllText(target, "existing");
            Assert.Throws<System.ComponentModel.Win32Exception>(() => CopyCold(source, target));
            Assert.Equal("existing", File.ReadAllText(target));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    private static void CopyCold(string source, string target)
    {
        if (!CopyFileEx(source, target, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1001))
        {
            var error = Marshal.GetLastPInvokeError();
            // FAIL_IF_EXISTS preserves any existing file. Other failures can leave a partial new copy.
            if (error is not (80 or 183)) File.Delete(target);
            throw new System.ComponentModel.Win32Exception(error, "Unable to create isolated pressure fixture.");
        }
    }
#pragma warning disable SYSLIB1054 // The test project does not enable unsafe code required by LibraryImport.
    [DllImport("kernel32.dll", EntryPoint = "CopyFileExW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(string existing, string destination, IntPtr progress, IntPtr data, IntPtr cancel, uint flags);
#pragma warning restore SYSLIB1054
}

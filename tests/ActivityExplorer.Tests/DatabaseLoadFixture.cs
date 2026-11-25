using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

// Isolated, synthetic service-load data. History scales activities per two owners;
// profiles scales whole owner graphs. Fanout is intentionally fixed in both.
internal sealed class DatabaseLoadFixture : IAsyncDisposable
{
    private const int ActivitiesPerGraph = 50;
    private static readonly string[] LifecycleTables =
        ["WatchedImportAdmissions", "ImportBatchCheckpoints", "OwnerProjectionVersions", "SegmentProjectionVersions"];
    private readonly string connectionString;
    private readonly DbContextOptions<ExplorerDbContext> options;
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public IDbContextFactory<ExplorerDbContext> Factory => new LoadFactory(options);
    public Guid OwnerId { get; } = Id("owner", 0, 0);
    public Guid ActivityId { get; } = Id("activity", 0, 0);
    public Guid SegmentId { get; } = Id("segment", 0, 0);
    public Guid ImportBatchId { get; } = Id("batch", 0, 0);
    public Guid TagId { get; } = Id("tag", 0, 0);
    public IReadOnlyDictionary<string, long> Counts { get; private set; } = new Dictionary<string, long>();

    private DatabaseLoadFixture(string path)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, DefaultTimeout = 30, Pooling = true }.ToString();
        options = new DbContextOptionsBuilder<ExplorerDbContext>().UseSqlite(connectionString).Options;
    }
    private sealed class LoadFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
    }
    private static Guid Id(string kind, int graph, int ordinal) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"fixture:{kind}:{graph}:{ordinal}"))[..16]);

    public static async Task<DatabaseLoadFixture> CreateAsync(string directory, int scale, string dimension)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);
        if (dimension is not ("history" or "profiles")) throw new ArgumentException("Choose history or profiles.", nameof(dimension));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"load-{dimension}-{scale}.db");
        // Atomic create protects existing fixtures from accidental replacement.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var fixture = new DatabaseLoadFixture(path);
        var ownsManifest = false;
        try
        {
            await fixture.SeedAsync(scale, dimension);
            await using var manifest = new FileStream(path + ".manifest", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsManifest = true;
            await using var writer = new StreamWriter(manifest);
            await writer.WriteAsync($"activity-explorer-load-v2:{dimension}:{scale}");
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
            if (ownsManifest) File.Delete(path + ".manifest");
            throw;
        }
    }

    public static async Task<DatabaseLoadFixture> OpenAsync(string directory, int scale, string dimension)
    {
        if (scale < 1 || dimension is not ("history" or "profiles")) throw new ArgumentException("Invalid fixture dimensions.");
        var path = Path.Combine(directory, $"load-{dimension}-{scale}.db");
        if (!File.Exists(path)) throw new InvalidOperationException("Fixture database is missing.");
        var legacy = await IsLegacyFixtureAsync(path, scale, dimension);
        var fixture = new DatabaseLoadFixture(path);
        try
        {
            await using var db = await fixture.Factory.CreateDbContextAsync();
            await fixture.ReadCountsAsync(db, legacy);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    internal static async Task<bool> IsLegacyFixtureAsync(string path, int scale, string dimension)
    {
        if (!File.Exists(path + ".manifest")) throw new InvalidOperationException("Fixture database manifest is missing.");
        var manifest = await File.ReadAllTextAsync(path + ".manifest");
        if (manifest == $"activity-explorer-load-v1:{dimension}:{scale}") return true;
        if (manifest == $"activity-explorer-load-v2:{dimension}:{scale}") return false;
        throw new InvalidOperationException("Fixture database manifest is incompatible.");
    }

    internal static bool IsLifecycleTable(string table) =>
        LifecycleTables.Contains(table, StringComparer.Ordinal);

    private async Task SeedAsync(int scale, string dimension)
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var points = Enumerable.Range(0, 60).Select(i => new TrackPoint(Epoch.AddSeconds(i * 10), 55.0 + i * .0001,
            12.0 + i * .0001, i * 100, 20 + i * .1, 10, 140, 85, 180, 15)).ToArray();
        // Cache a bounded distribution of physically aligned tracks while repeating the
        // same distribution at larger scales to make scale comparisons stable.
        var payloads = new Dictionary<(int Day, bool Power), byte[]>();
        byte[] Payload(int day, bool power)
        {
            if (!payloads.TryGetValue((day, power), out var payload))
            {
                payload = TrackCodec.Encode(points.Select(point => point with
                {
                    Timestamp = point.Timestamp!.Value.AddDays(day),
                    PowerWatts = power ? point.PowerWatts : null
                }).ToArray());
                payloads.Add((day, power), payload);
            }
            return payload;
        }
        var commonCell = ExplorationGrid.Extract(points, new HashSet<int>(), true)[0];
        var geometry = GeometryCodec.ToWkb(points)!;
        for (var graph = 0; graph < scale * 2; graph++)
        {
            var ownerNumber = dimension == "history" ? graph % 2 : graph;
            var owner = Id("owner", ownerNumber, 0);
            if (dimension == "profiles" || graph < 2)
                db.Owners.Add(new OwnerProfile { Id = owner, DisplayName = $"Athlete {ownerNumber}", TimeZoneId = ownerNumber % 2 == 0 ? "Europe/Copenhagen" : "UTC", CreatedAtUtc = Epoch, UpdatedAtUtc = Epoch });
            var batch = Id("batch", graph, 0);
            var tags = new[] { Id("tag", graph, 0), Id("tag", graph, 1) };
            var segment = Id("segment", graph, 0);
            var child = Id("segment", graph, 1);
            var first = Id("activity", graph, 0);
            var route = Id("route", graph, 0);
            db.ImportBatches.Add(new ImportBatch
            {
                Id = batch,
                OwnerId = owner,
                DisplayName = $"Import {graph}",
                StagedPath = $"staging/{batch:N}/history.zip",
                Status = ImportStatus.Completed,
                SourceKind = SourceKind.WatchedFolder,
                CreatedAtUtc = Epoch,
                StartedAtUtc = Epoch,
                CompletedAtUtc = Epoch,
                FilesDiscovered = ActivitiesPerGraph,
                ActivitiesCreated = ActivitiesPerGraph
            });
            db.Add(new WatchedImportAdmission
            {
                OwnerId = owner,
                BatchId = batch,
                Format = ".zip",
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"fixture:archive:{graph}")))
            });
            db.Add(new ImportBatchCheckpoint { BatchId = batch, NextEntry = ActivitiesPerGraph });
            db.Tags.AddRange(Enumerable.Range(0, 2).Select(i => new Tag { Id = tags[i], OwnerId = owner, Name = $"Tag {graph}-{i}", NormalizedName = $"TAG {graph}-{i}" }));
            db.SavedSearches.Add(new SavedSearch { Id = Id("search", graph, 0), OwnerId = owner, Name = $"Search {graph}", NormalizedName = $"SEARCH {graph}", CriteriaJson = "{}" });
            db.Gears.Add(new Gear { Id = Id("gear", graph, 0), OwnerId = owner, Name = $"Bike {graph}", Sport = SportKind.Cycling });
            db.WatchedFolders.Add(new WatchedFolder { Id = Id("folder", graph, 0), OwnerId = owner, Path = $"synthetic/{graph}" });
            var goal = Id("goal", graph, 0);
            db.Goals.Add(new PersonalGoal { Id = goal, OwnerId = owner, Recurrence = GoalRecurrence.Yearly, Start = new DateOnly(2020, 1, 1) });
            db.GoalDefinitionRevisions.AddRange(
                new GoalDefinitionRevision { GoalId = goal, EffectiveFromEdition = new DateOnly(2020, 1, 1), Name = "Synthetic goal", Target = 1000, Sport = SportKind.Cycling },
                new GoalDefinitionRevision { GoalId = goal, EffectiveFromEdition = new DateOnly(2021, 1, 1), Name = "Synthetic goal", Target = 1200, Sport = SportKind.Cycling });
            db.Routes.Add(new Route { Id = route, OwnerId = owner, SourceActivityId = first, Name = $"Route {graph}", Sport = SportKind.Cycling, DistanceMeters = 5900, GeometryWkb = geometry, MinLatitude = 55, MaxLatitude = 55.006, MinLongitude = 12, MaxLongitude = 12.006, CreatedAtUtc = Epoch });
            db.Segments.AddRange(new Segment { Id = segment, OwnerId = owner, SourceActivityId = first, Name = $"Segment {graph}", Sport = SportKind.Cycling, DistanceMeters = 3000, GeometryWkb = geometry, MinLatitude = 55, MaxLatitude = 55.006, MinLongitude = 12, MaxLongitude = 12.006, CreatedAtUtc = Epoch },
                new Segment { Id = child, OwnerId = owner, SourceActivityId = first, Name = $"Child {graph}", Sport = SportKind.Cycling, DistanceMeters = 1000, GeometryWkb = geometry, MinLatitude = 55, MaxLatitude = 55.006, MinLongitude = 12, MaxLongitude = 12.006, CreatedAtUtc = Epoch });
            db.SegmentLinks.Add(new SegmentLink { ParentSegmentId = segment, ChildSegmentId = child });
            for (var i = 0; i < ActivitiesPerGraph; i++)
            {
                var activity = Id("activity", graph, i);
                var source = Id("source", graph, i);
                var day = (int)((graph * (long)ActivitiesPerGraph + i) % 2400);
                var date = Epoch.AddDays(day);
                db.Activities.Add(new Activity { Id = activity, OwnerId = owner, Title = $"{(i % 5 == 0 ? "Hill Ride" : "Ride")} {graph}-{i}", Sport = i % 2 == 0 ? SportKind.Cycling : SportKind.Running, StartTimeUtc = date, NaturalFingerprint = $"fixture-{graph}-{i}", DistanceMeters = 5900 + i, MovingTimeSeconds = 590, ElapsedTimeSeconds = 590, ElevationGainMeters = 20, HasGps = true, HasPower = i % 2 == 0, DeviceName = "Synthetic device", GearName = "Synthetic bike", GeometryWkb = geometry, SimplifiedGeometryWkb = geometry, MinLatitude = 55, MaxLatitude = 55.006, MinLongitude = 12, MaxLongitude = 12.006, CreatedAtUtc = Epoch, UpdatedAtUtc = Epoch });
                db.ActivityStreams.Add(new ActivityStream { ActivityId = activity, OwnerId = owner, PointCount = 60, CompressedPayload = Payload(day, i % 2 == 0) });
                db.SourceFiles.Add(new SourceFile
                {
                    Id = source,
                    OwnerId = owner,
                    ImportBatchId = batch,
                    ActivityId = activity,
                    RouteId = i == 0 ? route : null,
                    SourceKind = SourceKind.WatchedFolder,
                    AcquisitionMethod = AcquisitionMethod.WatchedFolder,
                    OriginalName = $"ride-{graph}-{i}.fit",
                    StoredPath = $"synthetic/{graph}/{i}.fit",
                    Sha256 = $"{graph:x8}{i:x8}",
                    ImportedAtUtc = Epoch
                });
                for (var j = 0; j < 2; j++)
                {
                    db.ActivityLaps.Add(new ActivityLap { Id = Id("lap", graph, i * 2 + j), OwnerId = owner, ActivityId = activity, Sequence = j, DistanceMeters = 2950, ElapsedSeconds = 295, MovingSeconds = 295 });
                    db.ActivityMetrics.Add(new ActivityMetric { Id = Id("metric", graph, i * 2 + j), OwnerId = owner, ActivityId = activity, Key = $"metric-{j}", Label = $"Metric {j}", NumericValue = 100 + j, Origin = ActivityMetricOrigin.Imported, SourceFileId = source, CreatedAtUtc = Epoch, UpdatedAtUtc = Epoch });
                    db.ActivityTags.Add(new ActivityTag { ActivityId = activity, TagId = tags[j] });
                    db.ActivityExplorationCells.Add(new ActivityExplorationCell { ActivityId = activity, CellId = j == 0 ? commonCell : commonCell + 1 + graph % 1000000 });
                }
                db.ActivityExplorationIndexes.Add(new ActivityExplorationIndex { ActivityId = activity, ComputationVersion = ExplorationIndexService.ComputationVersion, CellCount = 2 });
                if (i % 2 == 0) db.SegmentEfforts.Add(new SegmentEffort { Id = Id("effort", graph, i), OwnerId = owner, SegmentId = segment, ActivityId = activity, EndPointIndex = 30, StartTimeUtc = date, ElapsedSeconds = 300 + i, MovingSeconds = 300, Rank = i + 1 });
            }
            // A snapshot has one winner per supported benchmark/profile, independent of history length.
            if (dimension == "profiles" || graph < 2)
                db.StatisticSnapshots.Add(new StatisticSnapshot { Id = Id("snapshot", graph, 0), OwnerId = owner, Sport = SportKind.Cycling, Kind = RecordKind.Distance, Key = "Longest distance", ActivityId = Id("activity", graph, 48), Value = 5948, ComputationVersion = RecordCatalog.ComputationVersion, CoveragePercent = 100, ComputedAtUtc = Epoch });
            db.FileOperations.Add(new FileOperationJournal { Id = Id("file-operation", graph, 0), OwnerId = owner, EntityId = first, Kind = FileOperationKind.Copy, State = FileOperationState.Completed, CreatedAtUtc = Epoch, UpdatedAtUtc = Epoch });
            db.McpOperations.Add(new McpOperation { Id = Id("operation", graph, 0), Action = "synthetic", RevisionToken = $"fixture-{graph}", EntityId = first, CreatedAtUtc = Epoch });
            db.McpEffects.Add(new McpEffect { Id = Id("operation", graph, 0), EntityId = first });
            db.ApplicationSettings.Add(new ApplicationSetting { Key = $"fixture-{graph}", Value = "synthetic", UpdatedAtUtc = Epoch });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        await transaction.CommitAsync();
        await ProjectionVersions.EnsureAsync(db, CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync(PublishSeededProjectionsSql);
        await ReadCountsAsync(db, legacy: false);
    }

    // These outputs are supplied by the fixture. Match their publication to the actual seeded inputs
    // rather than recomputing different winners as a side effect of preparing a measurement clone.
    internal const string PublishSeededProjectionsSql = """
        UPDATE OwnerProjectionVersions SET RecordsGeneration = InputGeneration,
            RecordsCount = (SELECT count(*) FROM StatisticSnapshots WHERE OwnerId = OwnerProjectionVersions.OwnerId);
        UPDATE SegmentProjectionVersions SET PublishedDefinitionGeneration = DefinitionGeneration,
            PublishedActivityGeneration = coalesce((
                SELECT o.InputGeneration FROM Segments s JOIN OwnerProjectionVersions o ON o.OwnerId = s.OwnerId
                WHERE s.Id = SegmentProjectionVersions.SegmentId), 0);
        """;

    private async Task ReadCountsAsync(ExplorerDbContext db, bool legacy)
    {
        var counts = new Dictionary<string, long>();
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await using (var reader = await command.ExecuteReaderAsync())
            if (await reader.ReadAsync()) throw new InvalidOperationException("Synthetic fixture violates a foreign key.");
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM ActivityStreams s JOIN Activities a ON a.Id=s.ActivityId WHERE s.OwnerId<>a.OwnerId) +
                (SELECT count(*) FROM ActivityLaps l JOIN Activities a ON a.Id=l.ActivityId WHERE l.OwnerId<>a.OwnerId) +
                (SELECT count(*) FROM ActivityMetrics m JOIN Activities a ON a.Id=m.ActivityId WHERE m.OwnerId<>a.OwnerId) +
                (SELECT count(*) FROM ActivityTags t JOIN Activities a ON a.Id=t.ActivityId JOIN Tags g ON g.Id=t.TagId WHERE a.OwnerId<>g.OwnerId) +
                (SELECT count(*) FROM SegmentEfforts e JOIN Activities a ON a.Id=e.ActivityId JOIN Segments s ON s.Id=e.SegmentId WHERE e.OwnerId<>a.OwnerId OR e.OwnerId<>s.OwnerId OR a.Sport<>s.Sport) +
                (SELECT count(*) FROM SourceFiles f JOIN Activities a ON a.Id=f.ActivityId JOIN ImportBatches b ON b.Id=f.ImportBatchId WHERE f.OwnerId<>a.OwnerId OR f.OwnerId<>b.OwnerId)
            """;
        if (Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Synthetic fixture has inconsistent ownership.");
        if (!legacy)
        {
            command.CommandText = $"""
                SELECT
                    (SELECT count(*) FROM ImportBatches b
                        LEFT JOIN WatchedImportAdmissions a ON a.BatchId=b.Id
                        LEFT JOIN ImportBatchCheckpoints p ON p.BatchId=b.Id
                        WHERE a.OwnerId IS NOT b.OwnerId OR a.Rejected IS NOT 0 OR a.Format IS NOT '.zip'
                            OR length(a.Sha256) IS NOT 64 OR b.SourceKind <> {(int)SourceKind.WatchedFolder}
                            OR b.Status <> {(int)ImportStatus.Completed} OR p.NextEntry IS NOT b.FilesDiscovered
                            OR b.ActivitiesCreated <> b.FilesDiscovered OR b.ActivitiesCreated <>
                                (SELECT count(*) FROM SourceFiles f WHERE f.ImportBatchId=b.Id
                                    AND f.SourceKind=b.SourceKind AND f.AcquisitionMethod={(int)AcquisitionMethod.WatchedFolder})) +
                    (SELECT count(*) FROM Owners o LEFT JOIN OwnerProjectionVersions v ON v.OwnerId=o.Id
                        WHERE v.InputGeneration IS NULL OR v.InputGeneration <> v.RecordsGeneration
                            OR v.RecordsCount <> (SELECT count(*) FROM StatisticSnapshots s WHERE s.OwnerId=o.Id)) +
                    (SELECT count(*) FROM Segments s JOIN OwnerProjectionVersions o ON o.OwnerId=s.OwnerId
                        LEFT JOIN SegmentProjectionVersions v ON v.SegmentId=s.Id
                        WHERE v.DefinitionGeneration IS NULL OR v.DefinitionGeneration <> v.PublishedDefinitionGeneration
                            OR o.InputGeneration <> v.PublishedActivityGeneration)
                """;
            if (Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Synthetic fixture has inconsistent import progress or stale projections.");
        }
        // Preserve the original count contract for frozen v1 comparisons.
        foreach (var table in db.Model.GetEntityTypes().Select(x => x.GetTableName()!)
                     .Where(table => table != "ActivityRecordEffortSummaries" && (!legacy || !IsLifecycleTable(table)))
                     .Concat(legacy ? [] : LifecycleTables).Distinct())
        {
            command.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\"";
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            if (count == 0) throw new InvalidOperationException($"Synthetic fixture does not cover {table}.");
            counts.Add(table, count);
        }
        Counts = counts;
    }

    public ValueTask DisposeAsync()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
        // The caller owns the persistent fixture files.
        return ValueTask.CompletedTask;
    }
}

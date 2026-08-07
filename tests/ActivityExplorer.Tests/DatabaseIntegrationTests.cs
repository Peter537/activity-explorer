using System.IO.Compression;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Import;
using Microsoft.Extensions.Logging.Abstractions;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ActivityExplorer.Tests;

public sealed class DatabaseIntegrationTests
{
    [Fact]
    public async Task Ensure_created_builds_the_expected_schema_without_migration_history()
    {
        var setup = await DatabaseSetup.CreateAsync();
        await using var db = await setup.Factory.CreateDbContextAsync();
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'").ToListAsync();
        Assert.Contains("Activities", tables);
        Assert.Contains("SourceFiles", tables);
        Assert.Contains("Segments", tables);
        Assert.DoesNotContain("__EFMigrationsHistory", tables);
        Assert.False(await db.Database.EnsureCreatedAsync());
    }

    [Fact]
    public async Task Initializer_is_idempotent_and_preserves_a_current_schema_database()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var previousDataRoot = Environment.GetEnvironmentVariable("ACTIVITY_EXPLORER_DATA");
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();

        try
        {
            var ownerId = await setup.SeedOwnerAsync("Current schema owner");
            await using (var db = await setup.Factory.CreateDbContextAsync())
            {
                db.ImportBatches.Add(new ImportBatch
                {
                    OwnerId = ownerId,
                    SourceKind = SourceKind.Gpx,
                    Status = ImportStatus.Running,
                    DisplayName = "Interrupted import",
                    StagedPath = string.Empty
                });
                await db.SaveChangesAsync();
            }

            var originals = new OriginalStore(paths);
            var fileOperations = new FileOperationCoordinator(
                setup.Factory, paths, originals, NullLogger<FileOperationCoordinator>.Instance);
            var initializer = new DatabaseInitializer(
                setup.Factory, paths, originals, fileOperations, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            await initializer.InitializeAsync();

            await using var verification = await setup.Factory.CreateDbContextAsync();
            Assert.Equal("Current schema owner", await verification.Owners
                .Where(owner => owner.Id == ownerId)
                .Select(owner => owner.DisplayName)
                .SingleAsync());
            var batch = await verification.ImportBatches.SingleAsync();
            Assert.Equal(ImportStatus.Interrupted, batch.Status);
            Assert.Null(batch.CompletedAtUtc);
            Assert.False(Directory.Exists(Path.Combine(paths.Root, "backups")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", previousDataRoot);
        }
    }

    [Fact]
    public async Task Activity_queries_are_owner_isolated()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = await setup.SeedOwnerAsync("One");
        var second = await setup.SeedOwnerAsync("Two");
        await setup.SeedActivityAsync(first, "First ride", SportKind.Cycling);
        await setup.SeedActivityAsync(second, "Second run", SportKind.Running);
        var statistics = new StatisticsService(setup.Factory);
        var segmentService = new SegmentService(setup.Factory, new SegmentMatcher());
        var storage = CreateStorageServices(setup);
        var service = new ActivityQueryService(
            setup.Factory, statistics, segmentService, storage.Originals, storage.FileOperations, storage.OwnerLock,
            NullLogger<ActivityQueryService>.Instance);
        var result = await service.SearchAsync(new ActivityFilter(OwnerId: first));
        Assert.Single(result.Items);
        Assert.Equal("First ride", result.Items[0].Title);
    }

    [Fact]
    public async Task Matching_activity_ids_honor_filters_and_stale_deletion_is_atomic()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("First");
        var secondOwner = await setup.SeedOwnerAsync("Second");
        var matching = await setup.SeedActivityAsync(firstOwner, "Delete this ride", SportKind.Cycling);
        await setup.SeedActivityAsync(firstOwner, "Keep this walk", SportKind.Walking);
        await setup.SeedActivityAsync(secondOwner, "Delete another ride", SportKind.Cycling);
        var storage = CreateStorageServices(setup);
        var service = new ActivityQueryService(
            setup.Factory,
            new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher()),
            storage.Originals,
            storage.FileOperations,
            storage.OwnerLock,
            NullLogger<ActivityQueryService>.Instance);

        var ids = await service.GetMatchingActivityIdsAsync(new ActivityFilter(
            OwnerId: firstOwner, Sport: SportKind.Cycling, Search: "Delete", Page: 4, PageSize: 1));

        Assert.Equal([matching], ids);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync([matching, Guid.NewGuid()]));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.True(await verification.Activities.AnyAsync(activity => activity.Id == matching));
    }

    [Fact]
    public async Task Activity_deletion_preserves_shared_files_unlinks_definitions_repairs_ranks_and_refreshes_statistics()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("First");
        var secondOwner = await setup.SeedOwnerAsync("Second");
        var deleteFirst = await setup.SeedActivityAsync(firstOwner, "Delete first", SportKind.Cycling);
        var retainFirst = await setup.SeedActivityAsync(firstOwner, "Retain first", SportKind.Cycling);
        var deleteSecond = await setup.SeedActivityAsync(secondOwner, "Delete second", SportKind.Running);
        var storage = CreateStorageServices(setup);
        var sharedBytes = System.Text.Encoding.UTF8.GetBytes("shared original");
        var uniqueBytes = System.Text.Encoding.UTF8.GetBytes("unique original");
        var sharedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sharedBytes));
        var uniqueHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(uniqueBytes));
        var sharedPath = storage.Originals.GetOriginalTarget(firstOwner, sharedHash, ".fit");
        var uniquePath = storage.Originals.GetOriginalTarget(secondOwner, uniqueHash, ".fit");
        await File.WriteAllBytesAsync(sharedPath, sharedBytes);
        await File.WriteAllBytesAsync(uniquePath, uniqueBytes);
        var geometry = GeometryCodec.ToWkb(TestSupport.Track(8))!;
        Guid routeId;
        Guid segmentId;
        Guid retainedEffortId;

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var firstBatch = new ImportBatch
            {
                OwnerId = firstOwner,
                SourceKind = SourceKind.Fit,
                Status = ImportStatus.Completed,
                DisplayName = "first.fit",
                StagedPath = string.Empty
            };
            var secondBatch = new ImportBatch
            {
                OwnerId = secondOwner,
                SourceKind = SourceKind.Fit,
                Status = ImportStatus.Completed,
                DisplayName = "second.fit",
                StagedPath = string.Empty
            };
            var route = new Route
            {
                OwnerId = firstOwner,
                SourceActivityId = deleteFirst,
                Sport = SportKind.Cycling,
                Name = "Saved route",
                DistanceMeters = 1000,
                GeometryWkb = geometry,
                MinLatitude = 1,
                MinLongitude = -30,
                MaxLatitude = 2,
                MaxLongitude = -29
            };
            var segment = new Segment
            {
                OwnerId = firstOwner,
                SourceActivityId = deleteFirst,
                Sport = SportKind.Cycling,
                Name = "Saved segment",
                DistanceMeters = 500,
                GeometryWkb = geometry,
                MinLatitude = 1,
                MinLongitude = -30,
                MaxLatitude = 2,
                MaxLongitude = -29
            };
            var deletedEffort = new SegmentEffort
            {
                OwnerId = firstOwner,
                Segment = segment,
                ActivityId = deleteFirst,
                StartTimeUtc = DateTimeOffset.UtcNow,
                StartPointIndex = 0,
                EndPointIndex = 5,
                ElapsedSeconds = 100,
                MovingSeconds = 100,
                Rank = 1
            };
            var retainedEffort = new SegmentEffort
            {
                OwnerId = firstOwner,
                Segment = segment,
                ActivityId = retainFirst,
                StartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                StartPointIndex = 0,
                EndPointIndex = 5,
                ElapsedSeconds = 200,
                MovingSeconds = 200,
                Rank = 2
            };
            db.AddRange(firstBatch, secondBatch, route, segment, deletedEffort, retainedEffort);
            db.SourceFiles.AddRange(
                new SourceFile
                {
                    OwnerId = firstOwner,
                    ImportBatch = firstBatch,
                    ActivityId = deleteFirst,
                    SourceKind = SourceKind.Fit,
                    Provider = SourceProvider.Garmin,
                    OriginalName = "shared.fit",
                    StoredPath = storage.Originals.ToStoredPath(sharedPath),
                    Sha256 = sharedHash,
                    Length = sharedBytes.Length
                },
                new SourceFile
                {
                    OwnerId = firstOwner,
                    ImportBatch = firstBatch,
                    ActivityId = retainFirst,
                    SourceKind = SourceKind.Fit,
                    Provider = SourceProvider.Strava,
                    OriginalName = "shared.fit",
                    StoredPath = storage.Originals.ToStoredPath(sharedPath),
                    Sha256 = new string('B', 64),
                    Length = sharedBytes.Length
                },
                new SourceFile
                {
                    OwnerId = secondOwner,
                    ImportBatch = secondBatch,
                    ActivityId = deleteSecond,
                    SourceKind = SourceKind.Fit,
                    Provider = SourceProvider.Garmin,
                    OriginalName = "unique.fit",
                    StoredPath = storage.Originals.ToStoredPath(uniquePath),
                    Sha256 = uniqueHash,
                    Length = uniqueBytes.Length
                });
            db.ActivityLaps.Add(new ActivityLap
            {
                OwnerId = firstOwner,
                ActivityId = deleteFirst,
                Sequence = 1,
                DistanceMeters = 1000,
                ElapsedSeconds = 100,
                MovingSeconds = 100
            });
            db.ActivityMetrics.Add(new ActivityMetric
            {
                OwnerId = firstOwner,
                ActivityId = deleteFirst,
                Key = "custom.delete",
                Label = "Delete me",
                NumericValue = 1,
                Origin = ActivityMetricOrigin.Manual
            });
            await db.SaveChangesAsync();
            routeId = route.Id;
            segmentId = segment.Id;
            retainedEffortId = retainedEffort.Id;
        }

        var statistics = new StatisticsService(setup.Factory);
        await statistics.RecomputeAsync(firstOwner);
        await statistics.RecomputeAsync(secondOwner);
        var service = new ActivityQueryService(
            setup.Factory,
            statistics,
            new SegmentService(setup.Factory, new SegmentMatcher()),
            storage.Originals,
            storage.FileOperations,
            storage.OwnerLock,
            NullLogger<ActivityQueryService>.Instance);

        var result = await service.DeleteAsync([deleteFirst, deleteSecond]);

        Assert.Equal(new ActivityDeletionResult(2, false, false), result);
        Assert.True(File.Exists(sharedPath));
        Assert.False(File.Exists(uniquePath));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal([retainFirst], await verification.Activities.Select(activity => activity.Id).ToArrayAsync());
        Assert.Single(await verification.SourceFiles.ToArrayAsync());
        Assert.Equal(retainFirst, (await verification.SourceFiles.SingleAsync()).ActivityId);
        Assert.Equal(2, await verification.ImportBatches.CountAsync());
        Assert.Null((await verification.Routes.SingleAsync(route => route.Id == routeId)).SourceActivityId);
        Assert.Null((await verification.Segments.SingleAsync(segment => segment.Id == segmentId)).SourceActivityId);
        var verifiedEffort = await verification.SegmentEfforts.SingleAsync(effort => effort.Id == retainedEffortId);
        Assert.Equal(1, verifiedEffort.Rank);
        Assert.Empty(await verification.ActivityLaps.ToArrayAsync());
        Assert.Empty(await verification.ActivityMetrics.ToArrayAsync());
        Assert.All(await verification.StatisticSnapshots.ToArrayAsync(), snapshot => Assert.Equal(retainFirst, snapshot.ActivityId));
        Assert.Contains(await verification.FileOperations.ToArrayAsync(), operation =>
            operation.Kind == FileOperationKind.FileQuarantine && operation.State == FileOperationState.Completed);
    }
    [Fact]
    public async Task Activity_transfer_blocks_provenance_collision_then_rehomes_sources_and_unlinks_definitions()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var sourceOwner = await setup.SeedOwnerAsync("Source owner");
        var targetOwner = await setup.SeedOwnerAsync("Target owner");
        var activityId = await setup.SeedActivityAsync(sourceOwner, "Transfer me", SportKind.Cycling);
        var storage = CreateStorageServices(setup, paths);
        var routes = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);
        var segments = new SegmentService(setup.Factory, new SegmentMatcher());
        var routeId = await routes.CreateFromActivityAsync(
            new CreateRouteRequest(sourceOwner, activityId, "Source route", null));
        var segmentId = await segments.CreateFromActivityAsync(
            new CreateSegmentRequest(sourceOwner, activityId, "Source segment", 2, 20));

        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("synthetic transfer source");
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes));
        var sourcePath = storage.Originals.GetOriginalTarget(sourceOwner, sourceHash, ".fit");
        await File.WriteAllBytesAsync(sourcePath, sourceBytes);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var sourceBatch = new ImportBatch
            {
                OwnerId = sourceOwner,
                SourceKind = SourceKind.Fit,
                Status = ImportStatus.Completed,
                DisplayName = "source.fit",
                StagedPath = string.Empty
            };
            var conflictBatch = new ImportBatch
            {
                OwnerId = targetOwner,
                SourceKind = SourceKind.Fit,
                Status = ImportStatus.Completed,
                DisplayName = "conflict.fit",
                StagedPath = string.Empty
            };
            db.ImportBatches.AddRange(sourceBatch, conflictBatch);
            db.SourceFiles.AddRange(
                new SourceFile
                {
                    OwnerId = sourceOwner,
                    ImportBatchId = sourceBatch.Id,
                    ActivityId = activityId,
                    SourceKind = SourceKind.Fit,
                    Provider = SourceProvider.Garmin,
                    OriginalName = "source.fit",
                    StoredPath = storage.Originals.ToStoredPath(sourcePath),
                    Sha256 = sourceHash,
                    ExternalId = "garmin-source-1",
                    Length = sourceBytes.Length
                },
                new SourceFile
                {
                    OwnerId = targetOwner,
                    ImportBatchId = conflictBatch.Id,
                    SourceKind = SourceKind.Fit,
                    Provider = SourceProvider.Garmin,
                    OriginalName = "conflict.fit",
                    StoredPath = "originals/conflict.fit",
                    Sha256 = new string('A', 64),
                    ExternalId = "garmin-source-1",
                    Length = 1
                });
            await db.SaveChangesAsync();
        }

        var activities = new ActivityQueryService(
            setup.Factory,
            new StatisticsService(setup.Factory),
            segments,
            storage.Originals,
            storage.FileOperations,
            storage.OwnerLock,
            NullLogger<ActivityQueryService>.Instance);
        var update = new UpdateActivityRequest("Transferred", null, null, targetOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => activities.UpdateAsync(activityId, update));

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var conflict = await db.SourceFiles.SingleAsync(x => x.OwnerId == targetOwner);
            db.SourceFiles.Remove(conflict);
            db.ImportBatches.Remove(await db.ImportBatches.SingleAsync(x => x.Id == conflict.ImportBatchId));
            await db.SaveChangesAsync();
        }
        await activities.UpdateAsync(activityId, update);

        await using var verification = await setup.Factory.CreateDbContextAsync();
        var moved = await verification.Activities.SingleAsync(x => x.Id == activityId);
        var movedSource = await verification.SourceFiles.SingleAsync(x => x.ActivityId == activityId);
        var transferBatch = await verification.ImportBatches.SingleAsync(x => x.Id == movedSource.ImportBatchId);
        Assert.Equal(targetOwner, moved.OwnerId);
        Assert.Equal(targetOwner, movedSource.OwnerId);
        Assert.Equal(ImportBatchKind.ActivityTransfer, transferBatch.Kind);
        Assert.Equal(ImportStatus.Completed, transferBatch.Status);
        Assert.False(Path.IsPathRooted(movedSource.StoredPath));
        Assert.True(File.Exists(storage.Originals.ResolveStoredPath(movedSource.StoredPath)));
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(sourceOwner, (await verification.Routes.SingleAsync(x => x.Id == routeId)).OwnerId);
        Assert.Null((await verification.Routes.SingleAsync(x => x.Id == routeId)).SourceActivityId);
        Assert.Equal(sourceOwner, (await verification.Segments.SingleAsync(x => x.Id == segmentId)).OwnerId);
        Assert.Null((await verification.Segments.SingleAsync(x => x.Id == segmentId)).SourceActivityId);
    }


    [Fact]
    public async Task Statistics_link_records_to_activity_with_coverage()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Athlete");
        var activity = await setup.SeedActivityAsync(owner, "Power ride", SportKind.Cycling, TestSupport.Track(180));
        var service = new StatisticsService(setup.Factory);
        await service.RecomputeAsync(owner);
        var records = await service.GetRecordsAsync(owner);
        Assert.Contains(records, x => x.Kind == RecordKind.Distance && x.ActivityId == activity);
        Assert.Contains(records, x => x.Kind == RecordKind.PowerCurve && x.Key == "5 s" && x.CoveragePercent == 100);
    }

    [Fact]
    public async Task Route_creation_and_gpx_export_are_local()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Router");
        var activity = await setup.SeedActivityAsync(owner, "Source", SportKind.Running);
        var storage = CreateStorageServices(setup);
        var service = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);
        var routeId = await service.CreateFromActivityAsync(new CreateRouteRequest(owner, activity, "Park loop", "Local"));
        var gpx = await service.ExportGpxAsync(routeId);
        Assert.Contains("Park loop", gpx);
        Assert.Contains("<rtept", gpx);
    }
    [Fact]
    public async Task Route_gpx_import_records_atomic_local_provenance()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var owner = await setup.SeedOwnerAsync("Route provenance");
        var stagingDirectory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var staged = TestSupport.Write(stagingDirectory, "route.gpx", TestSupport.Gpx());
        var points = await new GpxRouteReader().ReadAsync(staged);
        var hash = await Fingerprint.Sha256Async(staged, CancellationToken.None);
        var storage = CreateStorageServices(setup, paths);
        var service = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);

        var routeId = await service.ImportGpxAsync(
            new CreateRoutePathRequest(owner, "Imported route", null, SportKind.Running, points),
            staged,
            "route.gpx",
            hash,
            new FileInfo(staged).Length);

        await using var db = await setup.Factory.CreateDbContextAsync();
        var route = await db.Routes.Include(x => x.SourceFiles).SingleAsync(x => x.Id == routeId);
        var source = Assert.Single(route.SourceFiles);
        var batch = await db.ImportBatches.SingleAsync(x => x.Id == source.ImportBatchId);
        Assert.Equal(ImportBatchKind.RouteImport, batch.Kind);
        Assert.Equal(ImportStatus.Completed, batch.Status);
        Assert.False(Path.IsPathRooted(source.StoredPath));
        Assert.True(File.Exists(storage.Originals.ResolveStoredPath(source.StoredPath)));
        Assert.False(File.Exists(staged));
    }


    [Fact]
    public async Task Map_query_prunes_outside_bounding_box()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Mapper");
        await setup.SeedActivityAsync(owner, "Inside bounds", SportKind.Walking);
        var service = new MapFeatureService(setup.Factory);
        var inside = await service.GetActivitiesAsync(new MapQuery(owner, West: -31, South: 0, East: -29, North: 2));
        var outside = await service.GetActivitiesAsync(new MapQuery(owner, West: -10, South: -10, East: -9, North: -9));
        Assert.Single(inside.Features);
        Assert.Empty(outside.Features);
    }
    [Fact]
    public async Task Route_map_query_supports_antimeridian_viewports_and_rejects_nonfinite_bounds()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Dateline mapper");
        var storage = CreateStorageServices(setup);
        var routes = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);
        var points = TestSupport.Track(3).Select((point, index) => point with
        {
            Latitude = index * 0.01,
            Longitude = index == 1 ? -179.8 : 179.8
        }).ToArray();
        await routes.CreateAsync(new CreateRoutePathRequest(owner, "Dateline", null, SportKind.Cycling, points));
        var maps = new MapFeatureService(setup.Factory);

        var visible = await maps.GetRoutesAsync(new MapQuery(owner, West: 170, South: -10, East: -170, North: 10));
        var outside = await maps.GetRoutesAsync(new MapQuery(owner, West: 20, South: -10, East: 30, North: 10));

        Assert.Single(visible.Features);
        Assert.Empty(outside.Features);
        await Assert.ThrowsAsync<ArgumentException>(() => maps.GetRoutesAsync(
            new MapQuery(owner, West: double.NaN, South: -10, East: 30, North: 10)));
    }


    [Fact]
    public async Task Profile_deletion_removes_rows_and_copied_originals()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var owner = await setup.SeedOwnerAsync("Delete Me");
        await setup.SeedActivityAsync(owner, "Private", SportKind.Walking);
        var ownerPath = paths.GetOwnerOriginalsPath(owner);
        File.WriteAllText(Path.Combine(ownerPath, "original.gpx"), "private");
        var storage = CreateStorageServices(setup, paths);
        var service = new ProfileService(setup.Factory, paths, storage.FileOperations, storage.OwnerLock);
        await service.DeleteAsync(owner, "DELETE Delete Me");
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.False(await db.Owners.AnyAsync(x => x.Id == owner));
        Assert.False(await db.Activities.AnyAsync(x => x.OwnerId == owner));
        Assert.False(Directory.Exists(ownerPath));
    }
    [Fact]
    public async Task Profile_management_lists_exports_and_blocks_active_import_deletion()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var storage = CreateStorageServices(setup, paths);
        var service = new ProfileService(setup.Factory, paths, storage.FileOperations, storage.OwnerLock);
        var ownerId = await service.CreateAsync("Managed profile");

        Assert.Contains(await service.ListAsync(), profile => profile.Id == ownerId && profile.DisplayName == "Managed profile");
        var export = await service.ExportAsync(ownerId);
        Assert.EndsWith("-activity-explorer.json", export.FileName, StringComparison.Ordinal);
        Assert.Contains("Managed profile", export.Json, StringComparison.Ordinal);

        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.Add(new ImportBatch
            {
                OwnerId = ownerId,
                SourceKind = SourceKind.Gpx,
                Status = ImportStatus.Interrupted,
                DisplayName = "Pending",
                StagedPath = "staging/pending.gpx"
            });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(ownerId, "DELETE Managed profile"));
    }

    [Fact]
    public async Task Import_history_reports_owner_scoped_counts_and_terminal_summary()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var ownerId = await setup.SeedOwnerAsync("Historian");
        var batch = new ImportBatch
        {
            OwnerId = ownerId,
            SourceKind = SourceKind.GarminArchive,
            Status = ImportStatus.CompletedWithWarnings,
            DisplayName = "export.zip",
            StagedPath = string.Empty,
            FilesDiscovered = 5,
            ActivitiesCreated = 2,
            ActivitiesUpdated = 1,
            DuplicatesSkipped = 1,
            UnsupportedSkipped = 1,
            Warnings = 2,
            Summary = "Imported with warnings."
        };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync();
        }

        var service = new ImportHistoryService(setup.Factory);
        var history = await service.ListAsync(ownerId);
        Assert.Equal(1, history.Total);
        var summary = Assert.Single(history.Items);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(ImportStatus.CompletedWithWarnings, summary.Status);
        var report = await service.GetReportAsync(batch.Id);
        Assert.NotNull(report);
        Assert.Equal("Imported with warnings.", report.Summary);
        Assert.Null(await service.GetReportAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Import_history_returns_deterministic_cumulative_pages()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var ownerId = await setup.SeedOwnerAsync("Paged historian");
        var start = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            for (var index = 0; index < 15; index++)
            {
                db.ImportBatches.Add(new ImportBatch
                {
                    OwnerId = ownerId,
                    SourceKind = SourceKind.Gpx,
                    Status = ImportStatus.Completed,
                    DisplayName = $"Import {index:00}",
                    StagedPath = string.Empty,
                    CreatedAtUtc = start.AddMinutes(index)
                });
            }
            await db.SaveChangesAsync();
        }

        var service = new ImportHistoryService(setup.Factory);
        var first = await service.ListAsync(ownerId);
        var expanded = await service.ListAsync(ownerId, 20);

        Assert.Equal(15, first.Total);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(15, expanded.Items.Count);
        Assert.Equal("Import 14", first.Items[0].DisplayName);
        Assert.Equal("Import 00", expanded.Items[^1].DisplayName);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(ownerId, 0));
    }

    [Fact]
    public async Task Map_privacy_setting_defaults_blank_and_persists_explicit_opt_in()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var first = new MapSettingsService(setup.Factory);
        Assert.Equal(MapPrivacyMode.Blank, await first.GetModeAsync());
        Assert.Equal(MapPrivacyMode.Blank, await first.GetModeAsync());
        await first.SetModeAsync(MapPrivacyMode.OpenFreeMap);
        Assert.Equal(MapPrivacyMode.OpenFreeMap, await first.GetModeAsync());
        Assert.Equal(MapPrivacyMode.OpenFreeMap, await new MapSettingsService(setup.Factory).GetModeAsync());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            first.SetModeAsync((MapPrivacyMode)999));
    }


    [Fact]
    public async Task Garmin_then_exact_Strava_source_enriches_without_duplicate_activity()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var owner = await setup.SeedOwnerAsync("Importer");
        var importer = new MetadataImporter();

        async Task<Guid> AddBatchAsync(SourceKind sourceKind)
        {
            var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var staged = Path.Combine(directory, "same.fit");
            await File.WriteAllTextAsync(staged, "identical synthetic bytes");
            await using var db = await setup.Factory.CreateDbContextAsync();
            var batch = new ImportBatch
            {
                OwnerId = owner,
                SourceKind = sourceKind,
                DisplayName = sourceKind.ToString(),
                StagedPath = staged,
                Status = ImportStatus.Queued
            };
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync();
            return batch.Id;
        }

        var statistics = new StatisticsService(setup.Factory);
        var segments = new SegmentService(setup.Factory, new SegmentMatcher());
        var storage = CreateStorageServices(setup, paths);
        var processor = new ImportProcessor(
            setup.Factory, [importer], paths, storage.Originals, storage.FileOperations, storage.OwnerLock,
            statistics, segments, NullLogger<ImportProcessor>.Instance);

        await processor.ProcessAsync(await AddBatchAsync(SourceKind.GarminArchive));
        await processor.ProcessAsync(await AddBatchAsync(SourceKind.StravaArchive));

        await using var resultDb = await setup.Factory.CreateDbContextAsync();
        var activity = await resultDb.Activities.Include(x => x.SourceFiles).SingleAsync();
        Assert.Equal("Strava edited title", activity.Title);
        Assert.Equal("Strava description", activity.Description);
        Assert.Equal("strava-42", activity.StravaId);
        Assert.Equal(2, activity.SourceFiles.Count);
    }

    [Fact]
    public async Task Real_Garmin_then_gzipped_Strava_archive_enriches_idempotently()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var owner = await setup.SeedOwnerAsync("Archive importer");
        var fitBytes = await File.ReadAllBytesAsync(TestSupport.CyclingFit(setup.DataDirectory));

        string CreateGarminArchive()
        {
            var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var archivePath = Path.Combine(directory, "garmin-export.zip");
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("DI_CONNECT/DI_Connect-Fitness-Uploaded-Files/123456_ACTIVITY.fit");
            using var output = entry.Open();
            output.Write(fitBytes);
            return archivePath;
        }

        string CreateStravaArchive()
        {
            var directory = Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var archivePath = Path.Combine(directory, "strava-export.zip");
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var csv = archive.CreateEntry("activities.csv");
            using (var writer = new StreamWriter(csv.Open()))
            {
                writer.WriteLine("Activity ID,Activity Name,Activity Type,Activity Description,Filename,Activity Gear");
                writer.WriteLine("strava-42,Strava edited title,Ride,Strava description,activities/strava-42.fit.gz,Strava bike");
            }

            var activity = archive.CreateEntry("activities/strava-42.fit.gz");
            using var output = activity.Open();
            using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
            gzip.Write(fitBytes);
            return archivePath;
        }

        async Task<Guid> AddBatchAsync(string stagedPath, SourceKind sourceKind)
        {
            await using var db = await setup.Factory.CreateDbContextAsync();
            var batch = new ImportBatch
            {
                OwnerId = owner,
                SourceKind = sourceKind,
                DisplayName = Path.GetFileName(stagedPath),
                StagedPath = stagedPath,
                Status = ImportStatus.Queued
            };
            db.ImportBatches.Add(batch);
            await db.SaveChangesAsync();
            return batch.Id;
        }

        var archiveImporter = new ArchiveActivityImporter(
            paths,
            new FitActivityImporter(),
            new XmlActivityImporter());
        var storage = CreateStorageServices(setup, paths);
        var processor = new ImportProcessor(
            setup.Factory,
            [archiveImporter],
            paths,
            storage.Originals,
            storage.FileOperations,
            storage.OwnerLock,
            new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher()),
            NullLogger<ImportProcessor>.Instance);

        await processor.ProcessAsync(await AddBatchAsync(CreateGarminArchive(), SourceKind.GarminArchive));
        await processor.ProcessAsync(await AddBatchAsync(CreateStravaArchive(), SourceKind.StravaArchive));
        await processor.ProcessAsync(await AddBatchAsync(CreateStravaArchive(), SourceKind.StravaArchive));

        await using var resultDb = await setup.Factory.CreateDbContextAsync();
        var imported = await resultDb.Activities.Include(activity => activity.SourceFiles).SingleAsync();
        Assert.Equal("Strava edited title", imported.Title);
        Assert.Equal("Strava description", imported.Description);
        Assert.Equal("Strava bike", imported.GearName);
        Assert.Equal("123456", imported.GarminId);
        Assert.Equal("strava-42", imported.StravaId);
        Assert.Equal(2, imported.SourceFiles.Count);
        Assert.Equal(2, imported.SourceFiles.Select(source => source.Provider).Distinct().Count());
        Assert.Single(Directory.EnumerateFiles(paths.GetOwnerOriginalsPath(owner)));
    }

    private static (IOriginalStore Originals, IFileOperationCoordinator FileOperations, IOwnerMutationLock OwnerLock)
        CreateStorageServices(DatabaseSetup setup, AppDataPaths? existingPaths = null)
    {
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = existingPaths ?? new AppDataPaths();
        paths.EnsureCreated();
        var originals = new OriginalStore(paths);
        return (
            originals,
            new FileOperationCoordinator(
                setup.Factory,
                paths,
                originals,
                NullLogger<FileOperationCoordinator>.Instance),
            new OwnerMutationLock());
    }

    private sealed class MetadataImporter : IActivityImporter
    {
        public string Name => "Synthetic metadata importer";
        public bool CanImport(string path) => true;

        public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(
            string path, SourceKind sourceKind, CancellationToken cancellationToken = default)
        {
            var points = TestSupport.Track(30);
            var parsed = new ParsedActivity
            {
                Sport = SportKind.Cycling,
                Title = sourceKind == SourceKind.StravaArchive ? "Strava edited title" : "Garmin title",
                Description = sourceKind == SourceKind.StravaArchive ? "Strava description" : null,
                ExternalId = sourceKind == SourceKind.StravaArchive ? "strava-42" : "garmin-42",
                StartTimeUtc = points[0].Timestamp!.Value,
                DistanceMeters = 348,
                MovingTimeSeconds = 29,
                ElapsedTimeSeconds = 29,
                Points = points
            };
            var hash = await Fingerprint.Sha256Async(path, cancellationToken);
            var provider = sourceKind == SourceKind.StravaArchive ? SourceProvider.Strava : SourceProvider.Garmin;
            return [new ImportCandidate(path, "same.fit", sourceKind, hash, new FileInfo(path).Length, parsed,
                parsed.ExternalId, provider, AcquisitionMethod.AccountExport, FitActivityImporter.CurrentParserVersion)];
        }
    }

    private sealed class DatabaseSetup
    {
        private DatabaseSetup(string dataDirectory, TestDbFactory factory)
        {
            DataDirectory = dataDirectory;
            Factory = factory;
        }

        public string DataDirectory { get; }
        public TestDbFactory Factory { get; }

        public static async Task<DatabaseSetup> CreateAsync()
        {
            var directory = TestSupport.NewDirectory();
            var options = new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "test.db")}")
                .ConfigureWarnings(warnings => warnings
                    .Throw(RelationalEventId.MultipleCollectionIncludeWarning)
                    .Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
                .Options;
            var factory = new TestDbFactory(options);
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            return new DatabaseSetup(directory, factory);
        }

        public async Task<Guid> SeedOwnerAsync(string name)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var owner = new OwnerProfile { DisplayName = name };
            db.Owners.Add(owner);
            await db.SaveChangesAsync();
            return owner.Id;
        }

        public async Task<Guid> SeedActivityAsync(Guid ownerId, string title, SportKind sport, IReadOnlyList<TrackPoint>? track = null)
        {
            track ??= TestSupport.Track(60);
            var bounds = GeometryCodec.Bounds(track);
            var distance = GeometryCodec.DistanceMeters(track);
            var activity = new Core.Domain.Activity
            {
                OwnerId = ownerId,
                Title = title,
                Sport = sport,
                StartTimeUtc = track[0].Timestamp!.Value,
                NaturalFingerprint = Guid.NewGuid().ToString("N"),
                DistanceMeters = distance,
                MovingTimeSeconds = (track[^1].Timestamp!.Value - track[0].Timestamp!.Value).TotalSeconds,
                ElapsedTimeSeconds = (track[^1].Timestamp!.Value - track[0].Timestamp!.Value).TotalSeconds,
                ElevationGainMeters = 50,
                AveragePowerWatts = 230,
                HasPower = true,
                HasGps = true,
                MinLatitude = bounds.MinLat,
                MinLongitude = bounds.MinLon,
                MaxLatitude = bounds.MaxLat,
                MaxLongitude = bounds.MaxLon,
                GeometryWkb = GeometryCodec.ToWkb(track),
                SimplifiedGeometryWkb = GeometryCodec.ToWkb(track, 0.00001),
                Stream = new ActivityStream { OwnerId = ownerId, CompressedPayload = TrackCodec.Encode(track), PointCount = track.Count }
            };
            await using var db = await Factory.CreateDbContextAsync();
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
            return activity.Id;
        }
    }

    private sealed class TestDbFactory(DbContextOptions<ExplorerDbContext> options) : IDbContextFactory<ExplorerDbContext>
    {
        public ExplorerDbContext CreateDbContext() => new(options);
        public Task<ExplorerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExplorerDbContext(options));
    }
}

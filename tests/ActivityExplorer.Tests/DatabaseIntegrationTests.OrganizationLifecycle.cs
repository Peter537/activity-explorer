using System.Text.Json;
using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Any_tag_filters_share_complete_totals_rows_and_exact_ids_and_reject_unavailable_tags()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Tag filter athlete");
        var otherOwner = await setup.SeedOwnerAsync("Other tag athlete");
        var firstTag = new Tag { OwnerId = owner, Name = "Commute", NormalizedName = "COMMUTE" };
        var secondTag = new Tag { OwnerId = owner, Name = "Holiday", NormalizedName = "HOLIDAY" };
        var foreignTag = new Tag { OwnerId = otherOwner, Name = "Commute", NormalizedName = "COMMUTE" };
        var activities = Enumerable.Range(1, 48)
            .Select(index => ReportingActivity(owner, ReportingInstant("2026-10-09T10:00:00Z"), index)).ToArray();
        var expected = activities.Where((_, index) => index % 2 == 0 || index % 3 == 0).ToArray();
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Tags.AddRange(firstTag, secondTag, foreignTag);
            db.Activities.AddRange(activities);
            for (var index = 0; index < activities.Length; index++)
            {
                if (index % 2 == 0) db.ActivityTags.Add(new ActivityTag { ActivityId = activities[index].Id, TagId = firstTag.Id });
                if (index % 3 == 0) db.ActivityTags.Add(new ActivityTag { ActivityId = activities[index].Id, TagId = secondTag.Id });
            }
            await db.SaveChangesAsync();
        }
        var service = ReportingService(setup);
        var filter = new ActivityFilter(owner, SportKind.Cycling, new(2026, 10, 9), new(2026, 10, 9),
            "Target", true, "Synthetic", TagIds: [firstTag.Id, secondTag.Id, firstTag.Id]);
        var result = await service.SearchAsync(filter);
        var secondPage = await service.SearchAsync(filter with { Page = 2, Sort = "distance-desc" });

        Assert.Equal(32, result.Total);
        Assert.Equal(new ActivityTotals(expected.Length, expected.Sum(x => x.DistanceMeters),
            expected.Sum(x => x.MovingTimeSeconds), expected.Sum(x => x.ElevationGainMeters)), result.Totals);
        Assert.Equal(result.Totals, secondPage.Totals);
        Assert.Equal(expected.Select(x => x.Id).Order(), (await service.GetMatchingActivityIdsAsync(result.EffectiveFilter)).Order());
        Assert.All(result.Items.Concat(secondPage.Items), item => Assert.NotEmpty(item.Tags));
        Assert.Equal(2, (await service.GetAsync(activities[0].Id))!.Summary.Tags.Count);
        Assert.Contains((await service.SearchAsync(new ActivityFilter(PageSize: 100))).Items, item => item.Tags.Count > 0);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(filter with { OwnerId = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(filter with { TagIds = [foreignTag.Id] }));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Tags.Remove(await db.Tags.SingleAsync(x => x.Id == firstTag.Id));
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SearchAsync(filter));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMatchingActivityIdsAsync(result.EffectiveFilter));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("add-metric")]
    [InlineData("update-metric")]
    [InlineData("delete-metric")]
    public async Task Activity_metadata_and_manual_metric_changes_invalidate_the_whole_batch(string mutation)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Batch mutation athlete");
        var edited = await setup.SeedActivityAsync(owner, "Edited member", SportKind.Cycling);
        var retained = await setup.SeedActivityAsync(owner, "Untouched member", SportKind.Cycling);
        var services = OrganizationLifecycleServices(setup);
        var metric = new ActivityMetricRequest("effort", "Effort", 3, null, null);
        var metricId = mutation is "update-metric" or "delete-metric"
            ? await services.Activities.AddMetricAsync(edited, metric) : Guid.Empty;
        var review = await services.Organization.PrepareBatchAsync(owner, [edited, retained], null,
            new BatchChangeRequest([], [], GearEditMode.Set, "Reviewed bike"));

        switch (mutation)
        {
            case "metadata": await services.Activities.UpdateAsync(edited, new("Changed title", "New notes", null, owner)); break;
            case "add-metric": await services.Activities.AddMetricAsync(edited, metric); break;
            case "update-metric": await services.Activities.UpdateMetricAsync(edited, metricId, metric with { NumericValue = 4 }); break;
            case "delete-metric": await services.Activities.DeleteMetricAsync(edited, metricId); break;
        }

        Assert.True((await services.Activities.GetAsync(edited))!.Summary.MutationVersion > review.Members.Single(x => x.Id == edited).MutationVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Organization.ApplyBatchAsync(review));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.All(await verification.Activities.ToListAsync(), activity => Assert.Null(activity.GearName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activity_mutations_recheck_owner_after_an_intervening_transfer(bool metadata)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var sourceOwner = await setup.SeedOwnerAsync("Metric source");
        var targetOwner = await setup.SeedOwnerAsync("Metric target");
        var activityId = await setup.SeedActivityAsync(sourceOwner, "Moving metric activity", SportKind.Cycling);
        var services = OrganizationLifecycleServices(setup);
        var racingLock = new TransferBeforeAcquireLock(services.OwnerLock,
            () => services.Activities.UpdateAsync(activityId, new("Transferred activity", null, null, targetOwner)));
        var activities = new ActivityQueryService(setup.Factory, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), services.OwnerLock), services.Originals,
            services.Files, racingLock, NullLogger<ActivityQueryService>.Instance);

        if (metadata)
            await Assert.ThrowsAsync<InvalidOperationException>(() => activities.UpdateAsync(activityId,
                new("Stale title", null, null, sourceOwner)));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => activities.AddMetricAsync(activityId,
                new("effort", "Effort", 3, null, null)));

        await using var verification = await setup.Factory.CreateDbContextAsync();
        var transferred = await verification.Activities.SingleAsync(x => x.Id == activityId);
        Assert.Equal(targetOwner, transferred.OwnerId);
        Assert.Equal("Transferred activity", transferred.Title);
        Assert.Empty(await verification.ActivityMetrics.ToListAsync());
    }

    [Fact]
    public async Task Transfer_resolves_destination_tags_keeps_source_searches_and_profile_exports_and_deletion_are_isolated()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var sourceOwner = await setup.SeedOwnerAsync("Organization source");
        var targetOwner = await setup.SeedOwnerAsync("Organization target");
        var activityId = await setup.SeedActivityAsync(sourceOwner, "Tagged transfer", SportKind.Cycling);
        var services = OrganizationLifecycleServices(setup);
        var commute = await services.Organization.CreateTagAsync(sourceOwner, " Commute ");
        var holiday = await services.Organization.CreateTagAsync(sourceOwner, "Holiday");
        var existingTarget = await services.Organization.CreateTagAsync(targetOwner, "commute");
        await services.Organization.SetActivityTagsAsync(sourceOwner, activityId, [commute, holiday], 0);
        var criteria = new SavedSearchCriteria(1, SportKind.Cycling, null, null, null,
            [new(commute, "Commute"), new(holiday, "Holiday")], "start-desc", new(ReportingPreset.ThisMonth));
        var savedId = await services.Organization.SaveSavedSearchAsync(sourceOwner, "Tagged rides", criteria);
        var beforeTransfer = await services.Organization.PrepareBatchAsync(sourceOwner, [activityId], null,
            new([], [], GearEditMode.Set, "Source bike"));

        await services.Activities.UpdateAsync(activityId, new("Transferred ride", null, "Target bike", targetOwner));

        var detail = (await services.Activities.GetAsync(activityId))!;
        Assert.Equal(targetOwner, detail.Summary.OwnerId);
        Assert.Equal(2, detail.Summary.Tags.Count);
        Assert.Contains(detail.Summary.Tags, tag => tag.Id == existingTarget && tag.Name == "commute");
        Assert.Contains(detail.Summary.Tags, tag => tag.Id != holiday && tag.Name == "Holiday");
        Assert.Equal(savedId, Assert.Single(await services.Organization.ListSavedSearchesAsync(sourceOwner)).Id);
        Assert.Empty(await services.Organization.ListSavedSearchesAsync(targetOwner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Organization.ApplyBatchAsync(beforeTransfer));
        using (var exported = JsonDocument.Parse((await services.Profiles.ExportAsync(targetOwner)).Json))
        {
            Assert.Equal(1, exported.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(2, exported.RootElement.GetProperty("tags").GetArrayLength());
            Assert.Equal(2, exported.RootElement.GetProperty("activityTags").GetArrayLength());
            Assert.Equal(0, exported.RootElement.GetProperty("savedSearches").GetArrayLength());
        }
        using (var exported = JsonDocument.Parse((await services.Profiles.ExportAsync(sourceOwner)).Json))
        {
            Assert.Equal(0, exported.RootElement.GetProperty("activityTags").GetArrayLength());
            var saved = Assert.Single(exported.RootElement.GetProperty("savedSearches").EnumerateArray());
            Assert.Equal(1, saved.GetProperty("criteria").GetProperty("Version").GetInt32());
            Assert.Equal(2, saved.GetProperty("criteria").GetProperty("Tags").GetArrayLength());
        }

        await services.Profiles.DeleteAsync(sourceOwner, "DELETE Organization source");
        await using (var verification = await setup.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await verification.Tags.Where(x => x.OwnerId == sourceOwner).ToListAsync());
            Assert.Empty(await verification.SavedSearches.ToListAsync());
            Assert.Equal(2, await verification.ActivityTags.CountAsync(x => x.ActivityId == activityId));
        }
        await services.Activities.DeleteAsync([activityId]);
        await using var afterDeletion = await setup.Factory.CreateDbContextAsync();
        Assert.Empty(await afterDeletion.ActivityTags.ToListAsync());
        Assert.Equal(2, await afterDeletion.Tags.CountAsync(x => x.OwnerId == targetOwner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reimport_preserves_local_tags_and_gear_and_invalidates_review_including_parser_upgrades(bool upgradeParser)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Tagged importer");
        var services = OrganizationLifecycleServices(setup);
        var importer = new OrganizationLifecycleImporter();
        var processor = new ImportProcessor(setup.Factory, [importer], services.Paths, services.Originals,
            services.Files, services.OwnerLock, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), services.OwnerLock), NullLogger<ImportProcessor>.Instance);

        async Task ImportAsync(SourceKind kind)
        {
            var stagingDirectory = Path.Combine(services.Paths.StagingPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagedPath = Path.Combine(stagingDirectory, "same.fit");
            await File.WriteAllTextAsync(stagedPath, "Synthetic immutable tagged source");
            var batch = new ImportBatch { OwnerId = owner, SourceKind = kind, StagedPath = stagedPath, DisplayName = "Tagged fixture" };
            await using (var db = await setup.Factory.CreateDbContextAsync())
            {
                db.ImportBatches.Add(batch);
                await db.SaveChangesAsync();
            }
            await processor.ProcessAsync(batch.Id);
        }

        await ImportAsync(SourceKind.GarminArchive);
        var activity = Assert.Single((await services.Activities.SearchAsync(new(owner))).Items);
        var tag = await services.Organization.CreateTagAsync(owner, "Commute");
        await services.Organization.SetActivityTagsAsync(owner, activity.Id, [tag], activity.MutationVersion);
        await ImportAsync(SourceKind.StravaArchive);
        var enriched = (await services.Activities.GetAsync(activity.Id))!;
        Assert.Equal("Imported Strava title", enriched.Summary.Title);
        Assert.Equal(tag, Assert.Single(enriched.Summary.Tags).Id);

        var gear = await services.Organization.PrepareBatchAsync(owner, [activity.Id], null, new([], [], GearEditMode.Set, "Local bike"));
        await services.Organization.ApplyBatchAsync(gear);
        var pending = await services.Organization.PrepareBatchAsync(owner, [activity.Id], null, new([], [], GearEditMode.Clear, null));
        long explorationVersion;
        await using (var db = await setup.Factory.CreateDbContextAsync())
            explorationVersion = await db.Activities.Where(x => x.Id == activity.Id).Select(x => x.ExplorationInputVersion).SingleAsync();
        if (upgradeParser) importer.ParserVersion++;
        await ImportAsync(SourceKind.StravaArchive);

        var reimported = (await services.Activities.GetAsync(activity.Id))!;
        Assert.Equal("Local bike", reimported.GearName);
        Assert.Equal(tag, Assert.Single(reimported.Summary.Tags).Id);
        Assert.True(reimported.Summary.MutationVersion > pending.Members.Single().MutationVersion);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var afterImport = await db.Activities.Where(x => x.Id == activity.Id).Select(x => x.ExplorationInputVersion).SingleAsync();
            Assert.Equal(upgradeParser, afterImport > explorationVersion);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Organization.ApplyBatchAsync(pending));
        Assert.Single((await services.Activities.SearchAsync(new(owner))).Items);
        Assert.Single(Directory.EnumerateFiles(services.Paths.GetOwnerOriginalsPath(owner)));
    }

    private static (ActivityQueryService Activities, ActivityOrganizationService Organization, ProfileService Profiles,
        AppDataPaths Paths, IOriginalStore Originals, IFileOperationCoordinator Files, IOwnerMutationLock OwnerLock)
        OrganizationLifecycleServices(DatabaseSetup setup)
    {
        var storage = CreateStorageServices(setup);
        var paths = new AppDataPaths();
        var activities = new ActivityQueryService(setup.Factory, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), storage.Originals,
            storage.FileOperations, storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        return (activities, new ActivityOrganizationService(setup.Factory, storage.OwnerLock, activities),
            new ProfileService(setup.Factory, paths, storage.FileOperations, storage.OwnerLock), paths,
            storage.Originals, storage.FileOperations, storage.OwnerLock);
    }

    private sealed class OrganizationLifecycleImporter : IActivityImporter
    {
        public int ParserVersion { get; set; } = FitActivityImporter.CurrentParserVersion;
        public string Name => "Synthetic tagged importer";
        public bool CanImport(string path) => true;
        public async Task<IReadOnlyList<ImportCandidate>> ReadAsync(string path, SourceKind sourceKind, CancellationToken cancellationToken = default)
        {
            var points = TestSupport.Track(30);
            var parsed = new ParsedActivity
            {
                Sport = SportKind.Cycling,
                Title = sourceKind == SourceKind.StravaArchive ? "Imported Strava title" : "Imported Garmin title",
                GearName = "Imported bike",
                StartTimeUtc = points[0].Timestamp!.Value,
                DistanceMeters = 348,
                MovingTimeSeconds = 29,
                ElapsedTimeSeconds = 29,
                Points = points
            };
            return [new(path, "same.fit", sourceKind, await Fingerprint.Sha256Async(path, cancellationToken),
                new FileInfo(path).Length, parsed, "tagged-source", SourceProvider.Garmin,
                AcquisitionMethod.AccountExport, ParserVersion)];
        }
    }

    private sealed class TransferBeforeAcquireLock(IOwnerMutationLock inner, Func<Task> transfer) : IOwnerMutationLock
    {
        private bool _transferred;
        public async ValueTask<IAsyncDisposable> AcquireAsync(IEnumerable<Guid> ownerIds, CancellationToken cancellationToken = default)
        {
            if (!_transferred)
            {
                _transferred = true;
                await transfer();
            }
            return await inner.AcquireAsync(ownerIds, cancellationToken);
        }
    }
}

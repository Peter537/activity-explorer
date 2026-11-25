using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData("transfer")]
    [InlineData("edit")]
    [InlineData("reimport")]
    public async Task Reviewed_deletion_rejects_the_whole_selection_before_quarantine(string change)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("Reviewed source");
        var secondOwner = await setup.SeedOwnerAsync("Reviewed destination");
        var storage = CreateStorageServices(setup);
        var paths = TestSupport.DataPaths(setup.DataDirectory);
        var statistics = new StatisticsService(setup.Factory);
        var segments = new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock);
        var service = new ActivityQueryService(setup.Factory, statistics, segments, storage.Originals, storage.FileOperations,
            storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        var queue = new ImportQueue(setup.Factory, paths, storage.OwnerLock);
        var processor = new ImportProcessor(setup.Factory, [new XmlActivityImporter()], paths, storage.Originals, storage.FileOperations,
            storage.OwnerLock, statistics, segments, NullLogger<ImportProcessor>.Instance);
        var recording = TestSupport.Gpx(name: "Reviewed activity");
        await ImportAsync(firstOwner, recording);
        await ImportAsync(secondOwner, recording.Replace("2026-01-01", "2026-01-02", StringComparison.Ordinal));

        var displayed = await service.SearchAsync(new ActivityFilter(PageSize: 1));
        var review = await service.PrepareDeletionAsync(displayed.EffectiveFilter);
        Assert.Equal(2, review.Members.Count);
        var target = Assert.Single(review.Members, member => member.OwnerId == firstOwner);
        switch (change)
        {
            case "transfer": await service.UpdateReviewedAsync(target, new("Transferred", null, null, secondOwner)); break;
            case "edit": await service.UpdateReviewedAsync(target, new("Edited", null, null, firstOwner)); break;
            case "reimport": await ImportAsync(firstOwner, recording); break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteReviewedAsync(review));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        var activities = await verification.Activities.OrderBy(activity => activity.Id).ToArrayAsync();
        Assert.Equal(review.Members.Select(member => member.Id).Order(), activities.Select(activity => activity.Id));
        var changed = Assert.Single(activities, activity => activity.Id == target.Id);
        Assert.Equal(change == "transfer" ? secondOwner : firstOwner, changed.OwnerId);
        Assert.True(changed.MutationVersion > target.MutationVersion);
        var originals = await verification.SourceFiles.Select(source => source.StoredPath).ToArrayAsync();
        Assert.Equal(2, originals.Length);
        Assert.All(originals, path => Assert.True(File.Exists(storage.Originals.ResolveStoredPath(path))));
        Assert.False(await verification.FileOperations.AnyAsync(operation => operation.Kind == FileOperationKind.FileQuarantine));

        async Task ImportAsync(Guid owner, string content)
        {
            var directory = Directory.CreateDirectory(Path.Combine(paths.StagingPath, Guid.NewGuid().ToString("N"))).FullName;
            var staged = TestSupport.Write(directory, "recording.gpx", content);
            var batchId = await queue.EnqueueAsync(new ImportRequest(owner, staged, "recording.gpx", SourceKind.Gpx));
            await processor.ProcessAsync(batchId);
        }
    }

    [Fact]
    public async Task Reviewed_deletion_keeps_an_exact_immutable_selection_when_a_matching_activity_arrives()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Reviewed profile");
        var id = await setup.SeedActivityAsync(owner, "Reviewed ride", SportKind.Cycling);
        var storage = CreateStorageServices(setup);
        var service = new ActivityQueryService(setup.Factory, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), storage.Originals, storage.FileOperations,
            storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        var review = await service.PrepareDeletionAsync(new ActivityFilter(OwnerId: owner, Sport: SportKind.Cycling));
        var copied = review.Members.ToList();
        var selection = new ActivityDeletionReview(copied);
        var later = await setup.SeedActivityAsync(owner, "Later ride", SportKind.Cycling);
        copied.Clear();

        var result = await service.DeleteReviewedAsync(selection);

        Assert.Equal(1, result.DeletedCount);
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(id, Assert.Single(selection.Members).Id);
        Assert.Equal(later, await verification.Activities.Select(activity => activity.Id).SingleAsync());
    }

    [Fact]
    public async Task Reviewed_metadata_rejects_competing_edits_and_cannot_transfer_an_activity_back()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var firstOwner = await setup.SeedOwnerAsync("Original profile");
        var secondOwner = await setup.SeedOwnerAsync("Destination profile");
        var id = await setup.SeedActivityAsync(firstOwner, "Original title", SportKind.Cycling);
        var storage = CreateStorageServices(setup);
        var service = new ActivityQueryService(setup.Factory, new StatisticsService(setup.Factory),
            new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock), storage.Originals, storage.FileOperations,
            storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);

        var opened = ActivityMutationTarget.FromSummary((await service.GetAsync(id))!.Summary);
        await service.UpdateReviewedAsync(opened, new("First edit", "Current notes", "Current gear", firstOwner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateReviewedAsync(opened, new("Stale edit", null, null, firstOwner)));

        var latest = (await service.GetAsync(id))!;
        var transfer = ActivityMutationTarget.FromSummary(latest.Summary);
        Assert.Equal("First edit", latest.Summary.Title);
        Assert.Equal("Current notes", latest.Description);
        Assert.Equal("Current gear", latest.GearName);
        await service.UpdateReviewedAsync(transfer, new("Transferred", null, null, secondOwner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateReviewedAsync(transfer, new("Stale edit", null, null, firstOwner)));

        var current = (await service.GetAsync(id))!.Summary;
        Assert.Equal(secondOwner, current.OwnerId);
        Assert.Equal("Transferred", current.Title);
    }
}

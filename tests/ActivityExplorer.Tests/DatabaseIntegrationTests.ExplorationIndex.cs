using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Import;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Exploration_checkpoints_survive_cancellation_restart_and_mixed_scope_builds()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Exploration checkpoints");
        var other = await setup.SeedOwnerAsync("Other exploration checkpoints");
        await setup.SeedActivityAsync(owner, "First ride", SportKind.Cycling);
        await setup.SeedActivityAsync(owner, "Second ride", SportKind.Cycling);
        await setup.SeedActivityAsync(other, "Other run", SportKind.Running);
        var storage = CreateStorageServices(setup);
        var service = new ExplorationIndexService(setup.Factory, storage.OwnerLock, new ExplorationSourceReader(storage.Originals));
        Assert.Equal(0, (await service.GetStatusAsync(new())).IndexedActivities);
        using var cancelled = new CancellationTokenSource();
        var progress = new InlineExplorationProgress(value => { if (value.Completed == 1) cancelled.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BuildAsync(new(), progress, cancellationToken: cancelled.Token));
        Assert.Equal(1, (await service.GetStatusAsync(new())).IndexedActivities);

        var restarted = new ExplorationIndexService(setup.Factory, storage.OwnerLock, new ExplorationSourceReader(storage.Originals));
        await Task.WhenAll(restarted.BuildAsync(new(owner)), restarted.BuildAsync(new(other, SportKind.Running)), restarted.BuildAsync(new()));
        var status = await restarted.GetStatusAsync(new());
        Assert.True(status.IsComplete);
        Assert.Equal(3, status.IndexedActivities);
        Assert.Equal(3, status.LimitedActivities);
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(3, await db.ActivityExplorationIndexes.CountAsync());
        Assert.All(await db.ActivityExplorationIndexes.ToArrayAsync(), header => Assert.True(header.CellCount > 0));
    }

    [Fact]
    public async Task Exploration_zero_GPS_markers_complete_but_missing_GPS_streams_fail_without_a_checkpoint()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("No GPS exploration");
        var activity = new Activity
        {
            OwnerId = owner,
            Title = "Summary only",
            Sport = SportKind.Running,
            NaturalFingerprint = "summary",
            StartTimeUtc = DateTimeOffset.UtcNow
        };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.Add(activity);
            await db.SaveChangesAsync();
        }
        var storage = CreateStorageServices(setup);
        var service = new ExplorationIndexService(setup.Factory, storage.OwnerLock, new ExplorationSourceReader(storage.Originals));
        Assert.True((await service.BuildAsync(new(owner))).IsComplete);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.Equal(0, (await db.ActivityExplorationIndexes.SingleAsync()).CellCount);
            var broken = new Activity
            {
                OwnerId = owner,
                Title = "Missing stream",
                Sport = SportKind.Running,
                NaturalFingerprint = "broken",
                HasGps = true,
                StartTimeUtc = DateTimeOffset.UtcNow
            };
            db.Activities.Add(broken);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => service.BuildAsync(new(owner)));
        var status = await service.GetStatusAsync(new(owner));
        Assert.False(status.IsComplete);
        Assert.Equal(1, status.IndexedActivities);
    }

    [Fact]
    public async Task Exploration_rejects_an_empty_payload_when_the_canonical_stream_declares_samples()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Truncated exploration stream");
        var id = await setup.SeedActivityAsync(owner, "Truncated ride", SportKind.Cycling);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.ActivityStreams.SingleAsync(stream => stream.ActivityId == id)).CompressedPayload = [];
            await db.SaveChangesAsync();
        }
        var storage = CreateStorageServices(setup);
        using var service = new ExplorationIndexService(setup.Factory, storage.OwnerLock, new ExplorationSourceReader(storage.Originals));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.BuildAsync(new(owner)));
        Assert.Equal(0, (await service.GetStatusAsync(new(owner))).IndexedActivities);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("delete")]
    [InlineData("transfer")]
    public async Task Exploration_discards_input_changed_before_publication(string mutation)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Changing exploration");
        var other = await setup.SeedOwnerAsync("Destination exploration");
        var id = await setup.SeedActivityAsync(owner, "Changing track", SportKind.Cycling);
        var replacement = TestSupport.Track(3).Select(point => point with { Latitude = 40, Longitude = 20 }).ToArray();
        var storage = CreateStorageServices(setup);
        var mutationLock = new TransferBeforeAcquireLock(storage.OwnerLock, async () =>
        {
            await using var db = await setup.Factory.CreateDbContextAsync();
            var activity = await db.Activities.Include(x => x.Stream).SingleAsync(x => x.Id == id);
            if (mutation == "delete") db.Activities.Remove(activity);
            else if (mutation == "transfer")
            {
                activity.OwnerId = other;
                activity.Stream!.OwnerId = other;
            }
            else ImportProcessor.ReplaceTechnicalData(activity, new ParsedActivity
            {
                Sport = SportKind.Cycling,
                Title = activity.Title,
                StartTimeUtc = activity.StartTimeUtc,
                Points = replacement
            });
            await db.SaveChangesAsync();
        });
        var service = new ExplorationIndexService(setup.Factory, mutationLock, new ExplorationSourceReader(storage.Originals));
        Assert.True((await service.BuildAsync(new(owner))).IsComplete);
        await using var result = await setup.Factory.CreateDbContextAsync();
        var memberships = await result.ActivityExplorationCells.Where(x => x.ActivityId == id).Select(x => x.CellId).ToArrayAsync();
        if (mutation == "replace")
        {
            Assert.Equal(ExplorationGrid.Extract(replacement, new HashSet<int>(), false, CancellationToken.None), memberships.Order().ToArray());
            Assert.Equal(1, (await result.ActivityExplorationIndexes.SingleAsync()).InputVersion);
        }
        else Assert.Empty(memberships);
        if (mutation == "transfer") Assert.False((await service.GetStatusAsync(new(other))).IsComplete);
    }

    [Fact]
    public async Task Exploration_reuses_geometry_for_metadata_timezone_transfer_and_cascades_on_deletion()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Exploration metadata");
        var other = await setup.SeedOwnerAsync("Exploration recipient");
        var id = await setup.SeedActivityAsync(owner, "Ride", SportKind.Cycling);
        var retainedId = await setup.SeedActivityAsync(owner, "Retained until profile deletion", SportKind.Running);
        var services = OrganizationLifecycleServices(setup);
        var service = new ExplorationIndexService(setup.Factory, services.OwnerLock, new ExplorationSourceReader(services.Originals));
        await service.BuildAsync(new(owner));
        await services.Activities.UpdateAsync(id, new("Edited ride", "Note", "Bicycle", owner));
        var summary = (await services.Activities.GetAsync(id))!.Summary;
        var tag = await services.Organization.CreateTagAsync(owner, "Explored");
        await services.Organization.SetActivityTagsAsync(owner, id, [tag], summary.MutationVersion);
        await services.Profiles.UpdateTimeZoneAsync(owner, "UTC");
        Assert.True((await service.GetStatusAsync(new(owner))).IsComplete);
        await services.Activities.UpdateAsync(id, new("Transferred ride", null, null, other));
        Assert.True((await service.GetStatusAsync(new(other))).IsComplete);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.Equal(0, (await db.Activities.SingleAsync(x => x.Id == id)).ExplorationInputVersion);
            Assert.True(await db.ActivityExplorationCells.AnyAsync(x => x.ActivityId == id));
        }
        await services.Activities.DeleteAsync([id]);
        await services.Profiles.DeleteAsync(owner, "DELETE Exploration metadata");
        await using var final = await setup.Factory.CreateDbContextAsync();
        Assert.False(await final.Activities.AnyAsync(x => x.Id == retainedId));
        Assert.Empty(await final.ActivityExplorationCells.ToArrayAsync());
        Assert.Empty(await final.ActivityExplorationIndexes.ToArrayAsync());
    }

    [Fact]
    public async Task Exploration_rechecks_restored_originals_and_new_matching_sources_without_rewriting_streams()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Recovered exploration");
        const string firstPoint = "<trkpt lat=\"80\" lon=\"12\"><time>2026-01-01T10:00:00Z</time></trkpt>";
        const string secondPoint = "<trkpt lat=\"80\" lon=\"12.06\"><time>2026-01-01T10:00:30Z</time></trkpt>";
        var connected = $"<gpx><trk><type>cycling</type><trkseg>{firstPoint}{secondPoint}</trkseg></trk></gpx>";
        var interrupted = $"<gpx><trk><type>cycling</type><trkseg>{firstPoint}</trkseg><trkseg>{secondPoint}</trkseg></trk></gpx>";
        var staged = TestSupport.Write(setup.DataDirectory, "connected.gpx", connected);
        var parsed = Assert.Single(await new XmlActivityImporter().ReadAsync(staged, SourceKind.Gpx));
        var activityId = await setup.SeedActivityAsync(owner, "Recovered ride", SportKind.Cycling, parsed.Parsed.Points);
        var storage = CreateStorageServices(setup);
        var target = storage.Originals.GetOriginalTarget(owner, parsed.Sha256, ".gpx");
        Guid batchId;
        string fingerprint;
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var batch = new ImportBatch { OwnerId = owner, SourceKind = SourceKind.Gpx, Status = ImportStatus.Completed };
            batchId = batch.Id;
            db.ImportBatches.Add(batch);
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = owner,
                ActivityId = activityId,
                ImportBatchId = batch.Id,
                SourceKind = SourceKind.Gpx,
                OriginalName = "connected.gpx",
                StoredPath = storage.Originals.ToStoredPath(target),
                Sha256 = parsed.Sha256
            });
            fingerprint = TrackCodec.Fingerprint(await db.ActivityStreams.SingleAsync(x => x.ActivityId == activityId));
            await db.SaveChangesAsync();
        }
        var service = new ExplorationIndexService(setup.Factory, storage.OwnerLock, new ExplorationSourceReader(storage.Originals));
        Assert.Equal(1, (await service.BuildAsync(new(owner))).LimitedActivities);
        File.Copy(staged, target);
        Assert.Equal(1, (await service.BuildAsync(new(owner))).LimitedActivities);
        Assert.Equal(0, (await service.BuildAsync(new(owner), recheckLimited: true)).LimitedActivities);
        int connectedCount;
        await using (var db = await setup.Factory.CreateDbContextAsync())
            connectedCount = await db.ActivityExplorationCells.CountAsync(x => x.ActivityId == activityId);
        Assert.True(connectedCount > 2);

        var brokenFile = TestSupport.Write(setup.DataDirectory, "interrupted.gpx", interrupted);
        var brokenHash = await Fingerprint.Sha256Async(brokenFile, CancellationToken.None);
        var brokenTarget = storage.Originals.GetOriginalTarget(owner, brokenHash, ".gpx");
        File.Copy(brokenFile, brokenTarget);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.SourceFiles.Add(new SourceFile
            {
                OwnerId = owner,
                ActivityId = activityId,
                ImportBatchId = batchId,
                SourceKind = SourceKind.Gpx,
                OriginalName = "interrupted.gpx",
                StoredPath = storage.Originals.ToStoredPath(brokenTarget),
                Sha256 = brokenHash
            });
            (await db.Activities.SingleAsync(x => x.Id == activityId)).ExplorationInputVersion++;
            await db.SaveChangesAsync();
        }
        Assert.False((await service.GetStatusAsync(new(owner))).IsComplete);
        Assert.True((await service.BuildAsync(new(owner))).IsComplete);
        await using var result = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(2, await result.ActivityExplorationCells.CountAsync(x => x.ActivityId == activityId));
        Assert.Equal(fingerprint, TrackCodec.Fingerprint(await result.ActivityStreams.SingleAsync(x => x.ActivityId == activityId)));
        (await result.ActivityExplorationIndexes.SingleAsync()).ComputationVersion--;
        await result.SaveChangesAsync();
        Assert.False((await service.GetStatusAsync(new(owner))).IsComplete);
        Assert.True((await service.BuildAsync(new(owner))).IsComplete);
    }

    private sealed class InlineExplorationProgress(Action<ExplorationBuildProgress> report) : IProgress<ExplorationBuildProgress>
    {
        public void Report(ExplorationBuildProgress value) => report(value);
    }
}

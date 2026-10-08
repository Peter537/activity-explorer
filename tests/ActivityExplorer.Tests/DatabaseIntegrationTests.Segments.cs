using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Theory]
    [InlineData(SportKind.Cycling)]
    [InlineData(SportKind.Running)]
    [InlineData(SportKind.Walking)]
    [InlineData(SportKind.Rowing)]
    public async Task Subsegments_share_independent_history_and_support_nested_overlapping_sections(SportKind sport)
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Section athlete");
        var points = TestSupport.Track(120).Select((point, index) => point with
        {
            ElevationMeters = index < 40 ? 40 : index < 80 ? 80 - index : index - 80
        }).ToArray();
        var activity = await setup.SeedActivityAsync(owner, "Full outing", sport, points);
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var parent = await service.CreateFromActivityAsync(new CreateSegmentRequest(owner, activity, "Full path", 0, 119));
        var otherParent = await service.CreateFromActivityAsync(new CreateSegmentRequest(owner, activity, "Longer finish", 30, 119));
        var flat = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Flat stretch", 5, 30));
        var downhill = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Downhill", 42, 75));
        var child = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Finish", 80, 119));
        var grandchild = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(child, "Last stretch", 15, 39));
        var overlap = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Overlapping finish", 95, 119));
        var preview = await service.PreviewAttachmentAsync(otherParent, child);
        Assert.Equal(child, preview.Child.Id);
        Assert.Equal(50, Assert.Single(preview.Placements).StartIndex);
        await service.AttachAsync(otherParent, child);

        var solo = points.Skip(80).Select(point => point with { Timestamp = point.Timestamp!.Value.AddDays(1) }).ToArray();
        await setup.SeedActivityAsync(owner, "Section only", sport, solo);
        await service.RecomputeAsync(child);
        var detail = (await service.GetAsync(parent))!;
        Assert.Equal(new[] { flat, downhill, child, overlap }, detail.Children.Select(section => section.Summary.Id));
        Assert.Equal(0, detail.Children[0].Summary.AverageGrade);
        Assert.True(detail.Children[1].Summary.AverageGrade < 0);
        Assert.True(detail.Children[2].Summary.AverageGrade > 0);
        Assert.All(detail.Children, section => Assert.Single(section.Efforts));
        var childDetail = (await service.GetAsync(child))!;
        Assert.Equal(2, childDetail.Efforts.Count);
        Assert.Equal(2, childDetail.Parents.Count);
        Assert.Equal(grandchild, Assert.Single(childDetail.Children).Summary.Id);
        Assert.Equal(SegmentSourceKind.Segment, childDetail.Summary.SourceKind);
        var effortIds = childDetail.Efforts.Select(effort => effort.Id).Order().ToArray();
        await service.RecomputeAsync(child);
        Assert.Equal(effortIds, (await service.GetAsync(child))!.Efforts.Select(effort => effort.Id).Order());

        await service.DetachAsync(parent, child);
        childDetail = (await service.GetAsync(child))!;
        Assert.Equal(otherParent, Assert.Single(childDetail.Parents).Id);
        Assert.Equal(effortIds, childDetail.Efforts.Select(effort => effort.Id).Order());
        Assert.Single(childDetail.Children);
        Assert.DoesNotContain((await service.GetAsync(parent))!.Children, section => section.Summary.Id == child);
        Assert.Equal(5, (await service.ListRelationshipsAsync(owner)).Count);
        Assert.Empty(await service.ListRelationshipsAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Subsegment_breakdown_contains_all_and_only_passes_inside_the_selected_parent_effort()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Lap athlete");
        var start = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var loop = Enumerable.Range(0, 81).Select(index => new TrackPoint(start.AddSeconds(index),
            1 + 0.001 * Math.Sin(2 * Math.PI * index / 80), -30 + 0.001 * Math.Cos(2 * Math.PI * index / 80),
            null, null, null, null, null, null, null)).ToArray();
        var threeLaps = loop.Concat(loop.Skip(1)).Concat(loop.Skip(1))
            .Select((point, index) => point with { Timestamp = start.AddSeconds(index) }).ToArray();
        var firstActivity = await setup.SeedActivityAsync(owner, "Three laps", SportKind.Running, threeLaps);
        var slower = threeLaps.Select((point, index) => point with { Timestamp = start.AddDays(1).AddSeconds(index * 2) }).ToArray();
        var secondActivity = await setup.SeedActivityAsync(owner, "Slower laps", SportKind.Running, slower);
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var parent = await service.CreateFromActivityAsync(new CreateSegmentRequest(owner, firstActivity, "Two laps", 0, 160));
        var child = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Straight stretch", 20, 40));
        var detail = (await service.GetAsync(parent))!;
        Assert.Equal(2, detail.Efforts.Count);
        var section = Assert.Single(detail.Children);
        Assert.Equal(2, section.Placements.Count);
        Assert.Equal(6, section.Summary.EffortCount);
        Assert.Collection(section.Efforts, first => Assert.Equal(20, first.StartPointIndex), second => Assert.Equal(100, second.StartPointIndex));
        Assert.All(section.Efforts, effort => { Assert.Equal(firstActivity, effort.ActivityId); Assert.Equal(20, effort.ElapsedSeconds); });
        var secondEffort = detail.Efforts.Single(effort => effort.ActivityId == secondActivity);
        var switched = (await service.GetAsync(parent, secondEffort.Id))!;
        Assert.All(Assert.Single(switched.Children).Efforts, effort =>
        {
            Assert.Equal(secondActivity, effort.ActivityId);
            Assert.Equal(40, effort.ElapsedSeconds);
        });
        await using var db = await setup.Factory.CreateDbContextAsync();
        await db.SegmentEfforts.Where(effort => effort.SegmentId == child && effort.ActivityId == secondActivity).ExecuteDeleteAsync();
        Assert.Empty(Assert.Single((await service.GetAsync(parent, secondEffort.Id))!.Children).Efforts);
        Assert.Equal(3, (await service.GetAsync(child))!.Efforts.Count);
    }

    [Fact]
    public async Task Segment_links_reject_invalid_geometry_scope_duplicates_and_cycles()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Link athlete");
        var otherOwner = await setup.SeedOwnerAsync("Other link athlete");
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var points = TestSupport.Track(100);
        var parent = await service.CreateAsync(new CreateSegmentPathRequest(owner, "Parent", SportKind.Running, points));
        var child = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Child", 20, 70));
        var grandchild = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(child, "Grandchild", 10, 30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(parent, parent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(parent, child));
        var cycle = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(grandchild, parent));
        Assert.Contains("cycle", cycle.Message, StringComparison.Ordinal);
        var candidates = new[]
        {
            new CreateSegmentPathRequest(otherOwner, "Other profile", SportKind.Running, points.Skip(20).Take(30).ToArray()),
            new CreateSegmentPathRequest(owner, "Other sport", SportKind.Cycling, points.Skip(20).Take(30).ToArray()),
            new CreateSegmentPathRequest(owner, "Reverse", SportKind.Running, points.Skip(20).Take(30).Reverse().ToArray()),
            new CreateSegmentPathRequest(owner, "Full alias", SportKind.Running, points),
            new CreateSegmentPathRequest(owner, "Elsewhere", SportKind.Running, points.Select(point => point with { Latitude = point.Latitude + 1 }).ToArray())
        };
        foreach (var request in candidates)
        {
            var candidate = await service.CreateAsync(request);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PreviewAttachmentAsync(parent, candidate));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AttachAsync(parent, candidate));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Whole", 0, 99)));
        Assert.Equal(2, (await service.ListRelationshipsAsync(owner)).Count);
    }

    [Fact]
    public async Task Concurrent_attachment_is_serialized_and_does_not_duplicate_a_link()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Concurrent athlete");
        var ownerLock = new OwnerMutationLock();
        var first = new SegmentService(setup.Factory, new SegmentMatcher(), ownerLock);
        var second = new SegmentService(setup.Factory, new SegmentMatcher(), ownerLock);
        var parent = await first.CreateAsync(new CreateSegmentPathRequest(owner, "Parent", SportKind.Running, TestSupport.Track(100)));
        var child = await first.CreateAsync(new CreateSegmentPathRequest(owner, "Child", SportKind.Running, TestSupport.Track(100).Skip(20).Take(30).ToArray()));
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => first.AttachAsync(parent, child)).AsTask(),
            Record.ExceptionAsync(() => second.AttachAsync(parent, child)).AsTask());
        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is InvalidOperationException);
        Assert.Single(await first.ListRelationshipsAsync(owner));
    }

    [Fact]
    public async Task Subsegment_creation_rolls_back_definition_and_link_when_effort_generation_fails()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Atomic section athlete");
        var activity = await setup.SeedActivityAsync(owner, "Source", SportKind.Running, TestSupport.Track(100));
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), new OwnerMutationLock());
        var parent = await service.CreateFromActivityAsync(new CreateSegmentRequest(owner, activity, "Parent", 0, 99));
        var failing = new SegmentService(setup.Factory, new FailAfterPlacementMatcher(), new OwnerMutationLock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Failed child", 20, 60)));
        await using var db = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Segments.CountAsync());
        Assert.Equal(1, await db.SegmentEfforts.CountAsync());
        Assert.False(await db.SegmentLinks.AnyAsync());
    }

    [Fact]
    public async Task Profile_deletion_cleans_nested_links_without_affecting_another_profile()
    {
        var setup = await DatabaseSetup.CreateAsync();
        Environment.SetEnvironmentVariable("ACTIVITY_EXPLORER_DATA", setup.DataDirectory);
        var paths = new AppDataPaths();
        paths.EnsureCreated();
        var owner = await setup.SeedOwnerAsync("Delete sections");
        var other = await setup.SeedOwnerAsync("Keep sections");
        var storage = CreateStorageServices(setup, paths);
        var service = new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock);
        foreach (var id in new[] { owner, other })
        {
            var parent = await service.CreateAsync(new CreateSegmentPathRequest(id, "Parent", SportKind.Running, TestSupport.Track(100)));
            var child = await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(parent, "Child", 20, 70));
            await service.CreateSubsegmentAsync(new CreateSubsegmentRequest(child, "Grandchild", 10, 30));
        }
        await new ProfileService(setup.Factory, paths, storage.FileOperations, storage.OwnerLock).DeleteAsync(owner, "DELETE Delete sections");
        Assert.Empty(await service.ListRelationshipsAsync(owner));
        Assert.Equal(2, (await service.ListRelationshipsAsync(other)).Count);
        Assert.Equal(3, (await service.ListAsync(other)).Count);
    }

    private sealed class FailAfterPlacementMatcher : ISegmentMatcher
    {
        private int _calls;
        public Task<IReadOnlyList<SegmentMatch>> MatchAsync(IReadOnlyList<TrackPoint> activity,
            IReadOnlyList<TrackPoint> segment, double toleranceMeters, CancellationToken cancellationToken = default) =>
            ++_calls == 1 ? new SegmentMatcher().MatchAsync(activity, segment, toleranceMeters, cancellationToken)
                : throw new InvalidOperationException("Synthetic effort generation failure.");
    }
}

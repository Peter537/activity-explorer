using System.Text.Json;
using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using ActivityExplorer.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Organization_tags_preserve_owner_boundaries_names_and_missing_saved_references()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Organization owner");
        var other = await setup.SeedOwnerAsync("Other organization owner");
        var activity = await setup.SeedActivityAsync(owner, "Commute", SportKind.Cycling);
        var service = OrganizationService(setup);
        var tag = await service.CreateTagAsync(owner, "  Commute  ");
        var otherTag = await service.CreateTagAsync(other, "commute");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateTagAsync(owner, "cOmMuTe"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetActivityTagsAsync(owner, activity, [otherTag], 0));
        await service.SetActivityTagsAsync(owner, activity, [tag, tag], 0);
        await service.SetActivityTagsAsync(owner, activity, [tag], 1);
        var criteria = OrganizationCriteria([new(tag, "Commute")]);
        var savedId = await service.SaveSavedSearchAsync(owner, "  Cycling month  ", criteria);
        Assert.Equal(new TagSummary(tag, "Commute", 1, 1), Assert.Single(await service.ListTagsAsync(owner)));
        await service.RenameTagAsync(owner, tag, " Work ride ");
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var stored = await db.Activities.SingleAsync(x => x.Id == activity);
            Assert.Equal(2, stored.MutationVersion);
            Assert.False(stored.UserEdited);
        }
        var renamed = Assert.Single(await service.ListSavedSearchesAsync(owner));
        Assert.Equal("Work ride", Assert.Single(renamed.Criteria!.Tags).Name);
        Assert.Null(renamed.Error);
        await service.DeleteTagAsync(owner, tag);
        var unavailable = Assert.Single(await OrganizationService(setup).ListSavedSearchesAsync(owner));
        Assert.Equal(savedId, unavailable.Id);
        Assert.Null(unavailable.Error);
        Assert.Equal(new SavedTagReference(tag, "Work ride"), Assert.Single(unavailable.MissingTags));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.True(await db.Activities.AnyAsync(x => x.Id == activity));
            Assert.Empty(await db.ActivityTags.ToListAsync());
            Assert.Equal(3, await db.Activities.Where(x => x.Id == activity).Select(x => x.MutationVersion).SingleAsync());
        }
        await service.SaveSavedSearchAsync(owner, unavailable.Name, unavailable.Criteria! with { Tags = [] }, savedId);
        Assert.Empty(Assert.Single(await service.ListSavedSearchesAsync(owner)).MissingTags);
        Assert.Equal(otherTag, Assert.Single(await service.ListTagsAsync(other)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameSavedSearchAsync(other, savedId, "Foreign"));
        await service.RenameSavedSearchAsync(owner, savedId, "Current month");
        Assert.Equal("Current month", Assert.Single(await service.ListSavedSearchesAsync(owner)).Name);
        await service.DeleteSavedSearchAsync(owner, savedId);
        Assert.Empty(await service.ListSavedSearchesAsync(owner));
    }

    [Fact]
    public async Task Organization_saved_criteria_are_typed_validated_and_recover_from_corruption()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Saved criteria owner");
        var service = OrganizationService(setup);
        var tag = await service.CreateTagAsync(owner, "Holiday");
        var criteria = OrganizationCriteria([new(tag, "Old display name")]) with
        {
            Dates = new(ReportingPreset.ThisMonth, new(2020, 1, 1), new(2020, 1, 2)),
            Search = " ride ",
            Device = " device "
        };
        var saved = await service.SaveSavedSearchAsync(owner, "Relative month", criteria);
        var restored = Assert.Single(await OrganizationService(setup).ListSavedSearchesAsync(owner)).Criteria!;
        Assert.Equal(new ReportingDateSelection(ReportingPreset.ThisMonth), restored.Dates);
        Assert.Equal("Holiday", Assert.Single(restored.Tags).Name);
        Assert.Equal("ride", restored.Search);
        Assert.Equal("device", restored.Device);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSavedSearchAsync(owner, "relative MONTH", criteria));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSavedSearchAsync(owner, "Unsupported", criteria with { Version = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSavedSearchAsync(owner, "Bad sport", criteria with { Sport = (SportKind)999 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSavedSearchAsync(owner, "Bad sort", criteria with { Sort = "arbitrary" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSavedSearchAsync(owner, "Reversed", criteria with { Dates = new(ReportingPreset.Custom, new(2026, 3, 2), new(2026, 3, 1)) }));
        var custom = criteria with { Dates = new(ReportingPreset.Custom, new(2026, 3, 1), new(2026, 3, 31)) };
        await service.SaveSavedSearchAsync(owner, "Relative month", custom, saved);
        Assert.Equal(custom.Dates, Assert.Single(await service.ListSavedSearchesAsync(owner)).Criteria!.Dates);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            var json = (await db.SavedSearches.SingleAsync()).CriteriaJson;
            using var parsed = JsonDocument.Parse(json);
            Assert.False(parsed.RootElement.TryGetProperty("Page", out _));
            Assert.False(parsed.RootElement.TryGetProperty("OwnerId", out _));
            await db.SavedSearches.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CriteriaJson, "{damaged}"));
        }
        var broken = Assert.Single(await service.ListSavedSearchesAsync(owner));
        Assert.Null(broken.Criteria);
        Assert.NotNull(broken.Error);
        await service.SaveSavedSearchAsync(owner, "Repaired", custom, saved);
        Assert.Null(Assert.Single(await service.ListSavedSearchesAsync(owner)).Error);
    }

    [Fact]
    public async Task Organization_saved_relative_searches_advance_each_owner_month_while_custom_dates_stay_fixed()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Saved search east");
        var west = await setup.SeedOwnerAsync("Saved search west");
        var service = OrganizationService(setup);
        var eastTag = await service.CreateTagAsync(east, "Calendar");
        var westTag = await service.CreateTagAsync(west, "Calendar");
        var eastApril = ReportingActivity(east, ReportingInstant("2026-04-15T12:00:00Z"), 1);
        var eastBoundary = ReportingActivity(east, ReportingInstant("2026-04-30T22:30:00Z"), 2);
        var eastMay = ReportingActivity(east, ReportingInstant("2026-05-15T12:00:00Z"), 3);
        var eastJune = ReportingActivity(east, ReportingInstant("2026-06-15T12:00:00Z"), 4);
        var westApril = ReportingActivity(west, ReportingInstant("2026-04-15T12:00:00Z"), 5);
        var westBoundary = ReportingActivity(west, ReportingInstant("2026-04-30T22:30:00Z"), 6);
        var westMay = ReportingActivity(west, ReportingInstant("2026-05-15T12:00:00Z"), 7);
        var westJune = ReportingActivity(west, ReportingInstant("2026-06-15T12:00:00Z"), 8);
        Activity[] rows = [eastApril, eastBoundary, eastMay, eastJune, westApril, westBoundary, westMay, westJune];
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(owner => owner.Id == west)).TimeZoneId = "America/Los_Angeles";
            foreach (var row in rows)
                row.Tags.Add(new ActivityTag { ActivityId = row.Id, TagId = row.OwnerId == east ? eastTag : westTag });
            db.Activities.AddRange(rows);
            await db.SaveChangesAsync();
        }
        var eastCriteria = OrganizationCriteria([new(eastTag, "Calendar")]);
        var westCriteria = OrganizationCriteria([new(westTag, "Calendar")]);
        var fixedDates = new ReportingDateSelection(ReportingPreset.Custom, new(2026, 5, 1), new(2026, 5, 31));
        var eastRelative = await service.SaveSavedSearchAsync(east, "This month", eastCriteria);
        var westRelative = await service.SaveSavedSearchAsync(west, "This month", westCriteria);
        var eastCustom = await service.SaveSavedSearchAsync(east, "May", eastCriteria with { Dates = fixedDates });
        var westCustom = await service.SaveSavedSearchAsync(west, "May", westCriteria with { Dates = fixedDates });
        var clock = new ReportingClock(ReportingInstant("2026-04-30T22:30:00Z"));
        var queries = ReportingService(setup, clock);

        AssertSavedMatches(await Reopen(east, eastRelative), eastBoundary, eastMay);
        AssertSavedMatches(await Reopen(west, westRelative), westApril, westBoundary);
        AssertSavedMatches(await Reopen(east, eastCustom), eastBoundary, eastMay);
        AssertSavedMatches(await Reopen(west, westCustom), westMay);

        clock.Now = ReportingInstant("2026-05-31T22:30:00Z");
        AssertSavedMatches(await Reopen(east, eastRelative), eastJune);
        AssertSavedMatches(await Reopen(west, westRelative), westMay);
        AssertSavedMatches(await Reopen(east, eastCustom), eastBoundary, eastMay);
        AssertSavedMatches(await Reopen(west, westCustom), westMay);

        async Task<ActivitySearchResult> Reopen(Guid owner, Guid searchId)
        {
            var saved = (await OrganizationService(setup).ListSavedSearchesAsync(owner)).Single(search => search.Id == searchId);
            Assert.Null(saved.Error);
            Assert.Empty(saved.MissingTags);
            var criteria = Assert.IsType<SavedSearchCriteria>(saved.Criteria);
            return await queries.SearchAsync(new ActivityFilter(owner, criteria.Sport, criteria.Dates.From, criteria.Dates.To,
                criteria.Search, criteria.HasPower, criteria.Device, Sort: criteria.Sort, Period: criteria.Dates.Preset,
                TagIds: criteria.Tags.Select(tag => tag.Id).ToArray()));
        }

        static void AssertSavedMatches(ActivitySearchResult result, params Activity[] expected)
        {
            Assert.Equal(expected.Select(row => row.Id).Order(), result.Items.Select(row => row.Id).Order());
            Assert.Equal(expected.Length, result.Total);
            Assert.Equal(new ActivityTotals(expected.Length, expected.Sum(row => row.DistanceMeters),
                expected.Sum(row => row.MovingTimeSeconds), expected.Sum(row => row.ElevationGainMeters)), result.Totals);
        }
    }

    [Fact]
    public async Task Organization_batch_is_exact_across_chunks_and_rolls_back_every_chunk_on_failure()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Large organization owner");
        var rows = Enumerable.Range(0, 1103).Select(index => new Activity
        {
            OwnerId = owner,
            Title = $"Ride {index}",
            Sport = SportKind.Cycling,
            NaturalFingerprint = Guid.NewGuid().ToString("N"),
            StartTimeUtc = DateTimeOffset.UtcNow
        }).ToArray();
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Activities.AddRange(rows);
            await db.SaveChangesAsync();
        }
        var service = OrganizationService(setup);
        var tag = await service.CreateTagAsync(owner, "Reviewed");
        var review = await service.PrepareBatchAsync(owner, rows.Select(x => x.Id).ToArray(), null, new([tag], [], GearEditMode.Set, "  Local bike  "));
        var staleId = review.Members[^1].Id;
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Activities.Where(x => x.Id == staleId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.MutationVersion, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(review));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.Activities.AnyAsync(x => x.GearName != null || x.UserEdited));
            Assert.False(await db.Activities.AnyAsync(x => x.Id != staleId && x.MutationVersion != 0));
            Assert.Equal(1, await db.Activities.Where(x => x.Id == staleId).Select(x => x.MutationVersion).SingleAsync());
            Assert.False(await db.ActivityTags.AnyAsync());
            await db.Activities.Where(x => x.Id == staleId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.MutationVersion, 0));
        }
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailBatch BEFORE UPDATE ON Activities WHEN NEW.Title = 'Ride 1002' BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;");
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => service.ApplyBatchAsync(review));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.Activities.AnyAsync(x => x.GearName != null || x.MutationVersion != 0 || x.UserEdited));
            Assert.False(await db.ActivityTags.AnyAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailBatch");
        }
        using (var cancellation = new CancellationTokenSource())
        {
            var capture = new CancelAfterOrganizationChunk(cancellation);
            var factory = new TestDbFactory(new DbContextOptionsBuilder<ExplorerDbContext>()
                .UseSqlite($"Data Source={Path.Combine(setup.DataDirectory, "test.db")}").AddInterceptors(capture).Options);
            var cancellingService = OrganizationService(setup, factory);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellingService.ApplyBatchAsync(review, cancellation.Token));
            Assert.Equal(1, capture.CompletedSaves);
            Assert.True(capture.HadTransaction);
            Assert.InRange(capture.WrittenActivities, 1, rows.Length - 1);
            Assert.Equal(capture.WrittenActivities, capture.WrittenAssignments);
            await using var db = await setup.Factory.CreateDbContextAsync();
            Assert.False(await db.Activities.AnyAsync(x => x.GearName != null || x.MutationVersion != 0 || x.UserEdited));
            Assert.False(await db.ActivityTags.AnyAsync());
        }
        var result = await service.ApplyBatchAsync(review);
        Assert.Equal(new BatchEditResult(1103, 1103), result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(review));
        var idempotent = await service.PrepareBatchAsync(owner, rows.Select(x => x.Id).ToArray(), null, review.Changes);
        Assert.Equal(new BatchEditResult(1103, 0), await service.ApplyBatchAsync(idempotent));
        var clear = await service.PrepareBatchAsync(owner, [rows[0].Id], null, new([], [tag], GearEditMode.Clear, null));
        Assert.Equal(new BatchEditResult(1, 1), await service.ApplyBatchAsync(clear));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Equal(1102, await verification.ActivityTags.CountAsync());
        Assert.Equal(1102, await verification.Activities.CountAsync(x => x.GearName == "Local bike"));
        Assert.True(await verification.Activities.AllAsync(x => x.UserEdited));
        Assert.Equal(2, await verification.Activities.Where(x => x.Id == rows[0].Id).Select(x => x.MutationVersion).SingleAsync());
    }

    [Fact]
    public async Task Organization_filtered_review_excludes_later_matches_and_rejects_changed_tags_and_members()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Snapshot owner");
        var other = await setup.SeedOwnerAsync("Other snapshot owner");
        var first = await setup.SeedActivityAsync(owner, "Matching", SportKind.Cycling);
        var second = await setup.SeedActivityAsync(owner, "Matching", SportKind.Cycling);
        var foreign = await setup.SeedActivityAsync(other, "Matching", SportKind.Cycling);
        var service = OrganizationService(setup);
        var tag = await service.CreateTagAsync(owner, "Snapshot");
        var additions = new[] { tag };
        var change = new BatchChangeRequest(additions, [], GearEditMode.Unchanged, null);
        var review = await service.PrepareBatchAsync(owner, null, new(OwnerId: owner, Search: "Matching"), change);
        additions[0] = Guid.NewGuid();
        Assert.Equal(tag, Assert.Single(review.Changes.AddTagIds));
        change = change with { AddTagIds = [tag] };
        var later = await setup.SeedActivityAsync(owner, "Matching later", SportKind.Cycling);
        Assert.Equal(new BatchEditResult(2, 2), await service.ApplyBatchAsync(review));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.ActivityTags.AnyAsync(x => x.ActivityId == later || x.ActivityId == foreign));
            Assert.False(await db.Activities.AnyAsync(x => x.UserEdited));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareBatchAsync(owner, [first, foreign], null, change));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareBatchAsync(owner, null, new ActivityFilter(), change));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PrepareBatchAsync(owner, [first], null, change with { RemoveTagIds = [tag] }));
        var renameReview = await service.PrepareBatchAsync(owner, [later], null, change);
        await service.RenameTagAsync(owner, tag, "Renamed snapshot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(renameReview));
        var deletionReview = await service.PrepareBatchAsync(owner, [first, second], null, new([], [tag], GearEditMode.Set, "New bike"));
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Activities.Where(x => x.Id == second).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(deletionReview));
        await using var verification = await setup.Factory.CreateDbContextAsync();
        Assert.Null(await verification.Activities.Where(x => x.Id == first).Select(x => x.GearName).SingleAsync());
        Assert.True(await verification.ActivityTags.AnyAsync(x => x.ActivityId == first && x.TagId == tag));
    }

    private static SavedSearchCriteria OrganizationCriteria(IReadOnlyList<SavedTagReference> tags) =>
        new(1, SportKind.Cycling, null, null, null, tags, "distance-desc", new(ReportingPreset.ThisMonth));

    private static ActivityOrganizationService OrganizationService(DatabaseSetup setup, TestDbFactory? factory = null)
    {
        var storage = CreateStorageServices(setup);
        var contexts = factory ?? setup.Factory;
        var query = new ActivityQueryService(contexts, new StatisticsService(contexts),
            new SegmentService(contexts, new SegmentMatcher(), storage.OwnerLock), storage.Originals,
            storage.FileOperations, storage.OwnerLock, NullLogger<ActivityQueryService>.Instance);
        return new(contexts, storage.OwnerLock, query);
    }

    private sealed class CancelAfterOrganizationChunk(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public int CompletedSaves { get; private set; }
        public bool HadTransaction { get; private set; }
        public int WrittenActivities { get; private set; }
        public int WrittenAssignments { get; private set; }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            CompletedSaves++;
            var db = (ExplorerDbContext)eventData.Context!;
            HadTransaction = db.Database.CurrentTransaction is not null;
            WrittenActivities = await db.Activities.CountAsync(x => x.GearName == "Local bike" && x.MutationVersion == 1, CancellationToken.None);
            WrittenAssignments = await db.ActivityTags.CountAsync(CancellationToken.None);
            cancellation.Cancel();
            return result;
        }
    }
}

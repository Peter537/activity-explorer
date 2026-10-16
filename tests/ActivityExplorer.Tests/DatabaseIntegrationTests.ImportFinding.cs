using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Import_finding_filters_before_counts_and_cumulative_limit_without_affecting_upload_polling()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Import search owner");
        var other = await setup.SeedOwnerAsync("Other import search owner");
        var start = ReportingInstant("2026-10-24T22:00:00Z");
        var matching = Enumerable.Range(0, 16).Select(index => Batch(owner, start.AddMinutes(index), index)).ToArray();
        var wrongName = Batch(owner, start.AddHours(1), 20); wrongName.DisplayName = "Other filename.fit";
        var wrongStatus = Batch(owner, start.AddHours(1), 21); wrongStatus.Status = ImportStatus.Completed;
        var wrongWarnings = Batch(owner, start.AddHours(1), 22); wrongWarnings.Warnings = 0;
        var wrongSource = Batch(owner, start.AddHours(1), 23); wrongSource.SourceKind = SourceKind.Gpx;
        var wrongKind = Batch(owner, start.AddHours(1), 24); wrongKind.Kind = ImportBatchKind.RouteImport;
        var wrongDate = Batch(owner, start.AddDays(-1), 25);
        var foreign = Batch(other, start, 26);
        var active = Batch(owner, start.AddHours(2), 27); active.Status = ImportStatus.Running; active.DisplayName = "Active upload.fit";
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.AddRange(matching);
            db.ImportBatches.AddRange(wrongName, wrongStatus, wrongWarnings, wrongSource, wrongKind, wrongDate, foreign, active);
            await db.SaveChangesAsync();
        }
        var service = new ImportHistoryService(setup.Factory);
        var filter = new ImportHistoryFilter(owner, "  søEN  ", [ImportStatus.Failed, ImportStatus.CompletedWithWarnings], true,
            SourceKind.Fit, ImportBatchKind.FileImport, new(2026, 10, 25), new(2026, 10, 25));
        var first = await service.SearchAsync(filter);
        var expanded = await service.SearchAsync(filter, 20);
        Assert.Equal(16, first.Total);
        Assert.Equal(10, first.Items.Count);
        Assert.Equal(matching.Reverse().Select(x => x.Id), expanded.Items.Select(x => x.Id));
        Assert.Equal(expanded.Items.Take(10), first.Items);
        Assert.Equal(ImportStatus.Running, (await service.GetReportAsync(active.Id))!.Status);
        Assert.Single((await service.SearchAsync(filter with { HasWarnings = false })).Items);
        Assert.Equal(wrongSource.Id, Assert.Single((await service.SearchAsync(filter with { Source = SourceKind.Gpx })).Items).Id);
        Assert.Equal(wrongKind.Id, Assert.Single((await service.SearchAsync(filter with { Kind = ImportBatchKind.RouteImport })).Items).Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(filter, 0));

        static ImportBatch Batch(Guid ownerId, DateTimeOffset created, int index) => new()
        {
            OwnerId = ownerId,
            DisplayName = $"SØEN file {index:00}.fit",
            CreatedAtUtc = created,
            SourceKind = SourceKind.Fit,
            Status = index % 2 == 0 ? ImportStatus.CompletedWithWarnings : ImportStatus.Failed,
            Warnings = 2
        };
    }

    [Fact]
    public async Task Import_date_filters_use_each_owners_inclusive_created_date_across_DST()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var east = await setup.SeedOwnerAsync("Import east");
        var west = await setup.SeedOwnerAsync("Import west");
        var eastStart = ReportingInstant("2026-10-24T22:00:00Z");
        var eastEnd = ReportingInstant("2026-10-25T23:00:00Z");
        var westStart = ReportingInstant("2026-10-25T07:00:00Z");
        var westEnd = ReportingInstant("2026-10-26T07:00:00Z");
        var expected = new[] { Batch(east, eastStart), Batch(east, eastEnd.AddTicks(-1)), Batch(west, westStart), Batch(west, westEnd.AddTicks(-1)) };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            (await db.Owners.SingleAsync(x => x.Id == west)).TimeZoneId = "America/Los_Angeles";
            db.ImportBatches.AddRange(expected);
            db.ImportBatches.AddRange(Batch(east, eastStart.AddTicks(-1)), Batch(east, eastEnd), Batch(west, westStart.AddTicks(-1)), Batch(west, westEnd));
            await db.SaveChangesAsync();
        }
        var service = new ImportHistoryService(setup.Factory);
        var filter = new ImportHistoryFilter(From: new(2026, 10, 25), To: new(2026, 10, 25));
        var all = await service.SearchAsync(filter, 20);
        Assert.Equal(expected.Select(x => x.Id).Order(), all.Items.Select(x => x.Id).Order());
        Assert.Equal(2, (await service.SearchAsync(filter with { OwnerId = east })).Total);
        Assert.Equal(2, (await service.SearchAsync(filter with { OwnerId = west })).Total);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Owners.Where(x => x.Id == east).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.TimeZoneId, "Unavailable/Zone"));
        Assert.Equal(4, (await service.ListAsync(east)).Total);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(filter with { OwnerId = east }));

        static ImportBatch Batch(Guid owner, DateTimeOffset created) => new()
        {
            OwnerId = owner,
            CreatedAtUtc = created,
            DisplayName = "Boundary import.fit",
            SourceKind = SourceKind.Fit,
            Status = ImportStatus.Completed
        };
    }

    [Fact]
    public async Task Import_linked_counts_and_queries_deduplicate_retained_sources_and_respect_current_ownership()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Linked import owner");
        var other = await setup.SeedOwnerAsync("Foreign linked import owner");
        var batch = new ImportBatch { OwnerId = owner, DisplayName = "Retained results.zip", SourceKind = SourceKind.GarminArchive, ActivitiesCreated = 99 };
        var otherBatch = new ImportBatch { OwnerId = other, DisplayName = "Foreign results.zip", SourceKind = SourceKind.GarminArchive };
        var first = CriteriaActivity(owner, 1);
        var second = CriteriaActivity(owner, 2);
        var wrongSourceOwner = CriteriaActivity(owner, 3);
        var foreign = CriteriaActivity(other, 4);
        var route = new Route { OwnerId = owner, Name = "Linked route", Sport = SportKind.Cycling, GeometryWkb = GeometryCodec.ToWkb(TestSupport.Track(3))! };
        var foreignRoute = new Route { OwnerId = other, Name = "Foreign route", Sport = SportKind.Cycling };
        var wrongSourceRoute = new Route { OwnerId = owner, Name = "Wrong source owner route", Sport = SportKind.Cycling };
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.ImportBatches.AddRange(batch, otherBatch);
            db.Activities.AddRange(first, second, wrongSourceOwner, foreign);
            db.Routes.AddRange(route, foreignRoute, wrongSourceRoute);
            db.SourceFiles.AddRange(Source(owner, batch.Id, first.Id), Source(owner, batch.Id, first.Id), Source(owner, batch.Id, second.Id),
                Source(other, batch.Id, wrongSourceOwner.Id), Source(owner, batch.Id, foreign.Id),
                Source(owner, batch.Id, routeId: route.Id), Source(owner, batch.Id, routeId: foreignRoute.Id),
                Source(other, batch.Id, routeId: wrongSourceRoute.Id));
            await db.SaveChangesAsync();
        }
        var history = new ImportHistoryService(setup.Factory);
        var summary = Assert.Single((await history.ListAsync(owner)).Items);
        Assert.Equal(2, summary.LinkedActivityCount);
        Assert.Equal(1, summary.LinkedRouteCount);
        Assert.Equal(99, summary.Created);
        var queries = ReportingService(setup);
        var filter = new ActivityFilter(OwnerId: owner, ImportBatchId: batch.Id);
        var activities = await queries.SearchAsync(filter);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), activities.Items.Select(x => x.Id).Order());
        Assert.Equal(2, activities.Total);
        Assert.Equal(first.DistanceMeters + second.DistanceMeters, activities.Totals.DistanceMeters);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), (await queries.GetMatchingActivityIdsAsync(filter)).Order());
        var review = await OrganizationService(setup).PrepareBatchAsync(owner, null, filter, new([], [], GearEditMode.Set, "Reviewed gear"));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), review.Members.Select(x => x.Id).Order());
        var storage = CreateStorageServices(setup);
        var routes = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);
        Assert.Equal(route.Id, Assert.Single(await routes.SearchAsync(new(owner, ImportBatchId: batch.Id))).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchAsync(filter with { OwnerId = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queries.SearchAsync(filter with { OwnerId = other }));
        await Assert.ThrowsAsync<ArgumentException>(() => queries.SearchAsync(filter with { ImportBatchId = Guid.Empty }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => routes.SearchAsync(new(other, ImportBatchId: batch.Id)));
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            await db.SourceFiles.Where(x => x.ActivityId == first.Id).ExecuteDeleteAsync();
            await db.Activities.Where(x => x.Id == second.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.OwnerId, other));
        }
        var changed = Assert.Single((await history.ListAsync(owner)).Items);
        Assert.Equal(0, changed.LinkedActivityCount);
        Assert.Equal(1, changed.LinkedRouteCount);
        Assert.Empty((await queries.SearchAsync(filter)).Items);
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.ImportBatches.Where(x => x.Id == batch.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queries.SearchAsync(filter));
        await Assert.ThrowsAsync<InvalidOperationException>(() => routes.SearchAsync(new(owner, ImportBatchId: batch.Id)));

        static SourceFile Source(Guid sourceOwner, Guid batchId, Guid? activityId = null, Guid? routeId = null) => new()
        {
            OwnerId = sourceOwner,
            ImportBatchId = batchId,
            ActivityId = activityId,
            RouteId = routeId,
            SourceKind = SourceKind.Fit,
            Provider = SourceProvider.Garmin,
            OriginalName = "Retained source.fit",
            Sha256 = Guid.NewGuid().ToString("N")
        };
    }

    [Fact]
    public void Import_filter_rejects_invalid_modes_and_dates_and_normalizes_search_statuses()
    {
        ImportHistoryFilter[] invalid =
        [
            new(Statuses: [(ImportStatus)999]), new(Source: (SourceKind)999), new(Kind: (ImportBatchKind)999),
            new(From: new(2026, 10, 25), To: new(2026, 10, 24))
        ];
        foreach (var filter in invalid) Assert.Throws<ArgumentException>(() => ImportHistoryFiltering.Normalize(filter));
        var normalized = ImportHistoryFiltering.Normalize(new(Search: "  søen.fit  ", Statuses: [ImportStatus.Failed, ImportStatus.Failed]));
        Assert.Equal("søen.fit", normalized.Search);
        Assert.Equal([ImportStatus.Failed], normalized.Statuses);
        Assert.Null(ImportHistoryFiltering.Normalize(new(Search: " ")).Search);
        Assert.Throws<ArgumentNullException>(() => ImportHistoryFiltering.Normalize(null!));
    }
}

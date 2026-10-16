using ActivityExplorer.Core.Domain;
using ActivityExplorer.Infrastructure.Processing;
using ActivityExplorer.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Segment_detail_exposes_its_owners_reporting_timezone()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Segment timezone");
        await using (var db = await setup.Factory.CreateDbContextAsync())
            await db.Owners.Where(x => x.Id == owner).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.TimeZoneId, "America/Los_Angeles"));
        var activity = await setup.SeedActivityAsync(owner, "Segment source", SportKind.Running);
        var storage = CreateStorageServices(setup);
        var segments = new SegmentService(setup.Factory, new SegmentMatcher(), storage.OwnerLock);
        var segment = await segments.CreateFromActivityAsync(new(owner, activity, "Timezone section", 2, 20));
        Assert.Equal("America/Los_Angeles", (await segments.GetAsync(segment))!.OwnerTimeZoneId);
        Assert.NotEmpty((await segments.GetAsync(segment))!.Efforts);
        var unavailable = (await segments.GetAsync(segment, Guid.NewGuid()))!;
        Assert.Null(unavailable.SelectedEffortId);
        Assert.Empty(unavailable.SelectedEffortPoints);
    }
}

using ActivityExplorer.Core.Domain;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Services;

namespace ActivityExplorer.Tests;

public sealed partial class DatabaseIntegrationTests
{
    [Fact]
    public async Task Route_finding_combines_profile_sport_and_unicode_name_and_orders_ties_deterministically()
    {
        var setup = await DatabaseSetup.CreateAsync();
        var owner = await setup.SeedOwnerAsync("Route finder");
        var other = await setup.SeedOwnerAsync("Other route finder");
        var first = RouteRow(1, owner, "Park Æble", SportKind.Cycling, 5000, 200);
        var second = RouteRow(2, owner, "Park Æble", SportKind.Cycling, 5000, 200);
        var third = RouteRow(3, owner, "Park Ørn", SportKind.Cycling, 10000, 100);
        var fourth = RouteRow(4, owner, "Park løb", SportKind.Running, 2000, 50);
        var foreign = RouteRow(5, other, "Park Æble", SportKind.Cycling, 1000, 10);
        await using (var db = await setup.Factory.CreateDbContextAsync())
        {
            db.Routes.AddRange(second, fourth, foreign, third, first);
            await db.SaveChangesAsync();
        }
        var storage = CreateStorageServices(setup);
        var service = new RouteService(setup.Factory, storage.Originals, storage.FileOperations, storage.OwnerLock);

        Assert.Equal(new[] { first.Id, second.Id }, (await service.SearchAsync(new(owner, "  æBLE  ", SportKind.Cycling))).Select(route => route.Id));
        Assert.Single(await service.SearchAsync(new(owner, "park", SportKind.Running)));
        Assert.Equal(3, (await service.SearchAsync(new(Search: "ÆBLE"))).Count);
        Assert.Empty(await service.SearchAsync(new(owner, "not present")));
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await service.SearchAsync(new(owner, "Park", SportKind.Cycling, "name-asc"))).Select(route => route.Id));
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, (await service.SearchAsync(new(owner, "Park", SportKind.Cycling, "name-desc"))).Select(route => route.Id));
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await service.SearchAsync(new(owner, Sport: SportKind.Cycling, Sort: "distance-asc"))).Select(route => route.Id));
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, (await service.SearchAsync(new(owner, Sport: SportKind.Cycling, Sort: "distance-desc"))).Select(route => route.Id));
        Assert.Equal(new[] { third.Id, first.Id, second.Id }, (await service.SearchAsync(new(owner, Sport: SportKind.Cycling, Sort: "elevation-asc"))).Select(route => route.Id));
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await service.SearchAsync(new(owner, Sport: SportKind.Cycling, Sort: "elevation-desc"))).Select(route => route.Id));
        Assert.Equal((await service.SearchAsync(new(owner))).Select(route => route.Id), (await service.ListAsync(owner)).Select(route => route.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new(owner, Sport: (SportKind)999)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new(owner, Sort: "unsupported")));
    }

    private static Route RouteRow(int id, Guid owner, string name, SportKind sport, double distance, double ascent) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
        OwnerId = owner,
        Name = name,
        Sport = sport,
        DistanceMeters = distance,
        ElevationGainMeters = ascent
    };
}

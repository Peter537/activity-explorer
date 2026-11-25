using System.Security.Cryptography;
using ActivityExplorer.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Fact]
    public async Task Route_review_displays_the_specific_path_and_commits_the_reviewed_geometry()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var east = TestSupport.Track(4).Select((point, index) => point with { Latitude = 0, Longitude = 20 + index * .01 }).ToArray();
        var west = east.Select(point => point with { Longitude = point.Longitude - 80 }).ToArray();
        var first = await setup.Writes.PrepareAsync(Request("create_route_path", owner, new
        { name = "Reviewed route", sport = SportKind.Cycling, points = east }), CancellationToken.None);
        var second = await setup.Writes.PrepareAsync(Request("create_route_path", owner, new
        { name = "Reviewed route", sport = SportKind.Cycling, points = west }), CancellationToken.None);

        Assert.Equal(first.Details.GetProperty("effects").GetProperty("distanceMeters").GetDouble(),
            second.Details.GetProperty("effects").GetProperty("distanceMeters").GetDouble(), precision: 7);
        Assert.False(first.Details.GetProperty("requested").TryGetProperty("points", out _));
        var firstLocal = setup.Writes.GetLocalReview(first.PlanId).Path;
        var secondLocal = setup.Writes.GetLocalReview(second.PlanId).Path;
        Assert.NotNull(firstLocal);
        Assert.NotNull(secondLocal);
        Assert.Equal(east.Select(point => point.Longitude), firstLocal.Points.Select(point => point.Longitude));
        Assert.Equal(west.Select(point => point.Longitude), secondLocal.Points.Select(point => point.Longitude));
        Assert.NotEqual(firstLocal.Digest, secondLocal.Digest);

        var html = await setup.Client.GetStringAsync(first.ReviewUrl);
        Assert.Contains("Path to approve", html, StringComparison.Ordinal);
        await setup.Writes.ApproveAsync(first.PlanId, first.ConfirmationPhrase, CancellationToken.None);
        var completed = await WaitAsync(setup.Writes, first.PlanId);
        Assert.Equal("completed", completed.Status);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        var written = await db.Routes.Where(route => route.Id == completed.EntityId).Select(route => route.GeometryWkb).SingleAsync();
        Assert.NotNull(written);
        Assert.Equal(firstLocal.Digest, Convert.ToHexString(SHA256.HashData(written)));
    }
}

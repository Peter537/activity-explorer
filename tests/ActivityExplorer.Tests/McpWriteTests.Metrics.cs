using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Fact]
    public async Task Manual_metric_preview_checks_normalized_key_collisions_and_preserves_the_current_key()
    {
        await using var setup = await Setup.CreateAsync();
        var owner = setup.Fixture.Owner;
        var activityId = setup.Fixture.Activity;
        var activities = setup.Services.GetRequiredService<IActivityQueryService>();
        var metric = await activities.AddMetricAsync(activityId, new("Cadence Avg", "Cadence", 1, null, null));
        var other = await activities.AddMetricAsync(activityId, new("Other", "Other", 2, null, null));
        var collision = new ActivityMetricRequest("CADENCE-AVG", "Considered duplicate", 3, null, null);
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.PrepareAsync(
            Request("add_activity_metric", owner, new { activityId, request = collision }), CancellationToken.None));
        var duplicateEdit = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.PrepareAsync(
            Request("update_activity_metric", owner, new { activityId, metricId = other, request = collision }), CancellationToken.None));
        Assert.Equal(duplicate.Message, duplicateEdit.Message);
        Assert.Contains("key already exists", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(duplicate.Message, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activities.AddMetricAsync(activityId, collision))).Message);
        Assert.Equal(duplicate.Message, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activities.UpdateMetricAsync(activityId, other, collision))).Message);

        var allowed = await setup.Writes.PrepareAsync(Request("update_activity_metric", owner,
            new { activityId, metricId = metric, request = collision with { Label = "Updated cadence" } }), CancellationToken.None);
        await setup.Writes.ApproveAsync(allowed.PlanId, null, CancellationToken.None);
        Assert.Equal("completed", (await WaitAsync(setup.Writes, allowed.PlanId)).Status);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync();
        var updated = await db.ActivityMetrics.SingleAsync(value => value.Id == metric);
        Assert.Equal("custom.cadence-avg", updated.Key);
        Assert.Equal("Updated cadence", updated.Label);

        var newMetric = new ActivityMetricRequest("new key", "MCP key", 4, null, null);
        var stale = await setup.Writes.PrepareAsync(Request("add_activity_metric", owner, new { activityId, request = newMetric }), CancellationToken.None);
        await activities.AddMetricAsync(activityId, newMetric with { Label = "Web key" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Writes.ApproveAsync(stale.PlanId, null, CancellationToken.None));
        Assert.False(await db.McpOperations.AnyAsync(operation => operation.Id == stale.PlanId));
        Assert.Equal("Web key", await db.ActivityMetrics.Where(value => value.ActivityId == activityId && value.Key == "custom.new-key")
            .Select(value => value.Label).SingleAsync());

        var separateProfile = await setup.Writes.PrepareAsync(Request("add_activity_metric", setup.Fixture.OtherOwner,
            new { activityId = setup.Fixture.OtherActivity, request = collision }), CancellationToken.None);
        Assert.NotNull(separateProfile);
    }
}

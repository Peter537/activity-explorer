using ActivityExplorer.Core.Contracts;
using ActivityExplorer.Core.Models;
using ActivityExplorer.Infrastructure.Storage;
using ActivityExplorer.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ActivityExplorer.Tests;

public sealed partial class McpWriteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_approval_and_decline_report_the_serialized_winner(bool approveFirst)
    {
        using var gate = new PausableDecisionGate();
        await using var setup = await Setup.CreateAsync(configure: builder => builder.ConfigureServices(services =>
            services.AddSingleton<IOwnerMutationLock>(gate)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var plan = await setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.Owner, new { name = "One decision" }), token);
        var checkpoint = gate.PauseNext();

        Task<McpOperationStatus> approval;
        Task<McpApplyResult> decline;
        try
        {
            if (approveFirst)
            {
                approval = setup.Writes.ApproveAsync(plan.PlanId, null, token);
                await checkpoint.Entered.Task.WaitAsync(token);
                decline = setup.Writes.DeclineAsync(plan.PlanId, token);
                Assert.False(decline.IsCompleted);
            }
            else
            {
                decline = setup.Writes.DeclineAsync(plan.PlanId, token);
                await checkpoint.Entered.Task.WaitAsync(token);
                approval = setup.Writes.ApproveAsync(plan.PlanId, null, token);
                Assert.False(approval.IsCompleted);
            }
        }
        finally { checkpoint.Release.TrySetResult(); }

        var outcome = await decline;
        var repeat = await setup.Writes.DeclineAsync(plan.PlanId, token);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync(token);
        if (approveFirst)
        {
            Assert.Equal(plan.PlanId, (await approval).OperationId);
            Assert.Equal(plan.PlanId, outcome.Operation?.OperationId);
            Assert.Equal(plan.PlanId, repeat.Operation?.OperationId);
            Assert.Equal(plan.PlanId, (await setup.Writes.ApproveAsync(plan.PlanId, null, token)).OperationId);
            Assert.Equal("completed", (await WaitAsync(setup.Writes, plan.PlanId)).Status);
            Assert.Equal(1, await db.McpOperations.CountAsync(token));
            Assert.Equal(1, await db.Tags.CountAsync(tag => tag.Name == "One decision", token));
        }
        else
        {
            Assert.Equal(McpPlanClosure.Declined, (await Assert.ThrowsAsync<McpPlanClosedException>(() => approval)).Closure);
            Assert.Equal("declined", outcome.Status);
            Assert.Null(outcome.Operation);
            Assert.Equal(outcome, repeat);
            Assert.Empty(await db.McpOperations.ToArrayAsync(token));
            Assert.False(await db.Tags.AnyAsync(tag => tag.Name == "One decision", token));
        }
    }

    [Fact]
    public async Task Expiry_wins_when_the_decision_waits_past_the_plan_deadline()
    {
        using var gate = new PausableDecisionGate();
        var clock = new AdjustableClock();
        await using var setup = await Setup.CreateAsync(clock: clock, configure: builder => builder.ConfigureServices(services =>
            services.AddSingleton<IOwnerMutationLock>(gate)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var plan = await setup.Writes.PrepareAsync(Request("create_tag", setup.Fixture.Owner, new { name = "Must expire" }), token);
        var checkpoint = gate.PauseNext();
        var approval = setup.Writes.ApproveAsync(plan.PlanId, null, token);
        try
        {
            await checkpoint.Entered.Task.WaitAsync(token);
            clock.Advance(TimeSpan.FromMinutes(15));
        }
        finally { checkpoint.Release.TrySetResult(); }

        Assert.Equal(McpPlanClosure.Expired, (await Assert.ThrowsAsync<McpPlanClosedException>(() => approval)).Closure);
        var declined = await Assert.ThrowsAsync<McpPlanClosedException>(() => setup.Writes.DeclineAsync(plan.PlanId, token));
        Assert.Equal(McpPlanClosure.Expired, declined.Closure);
        await using var db = await setup.Fixture.Factory.CreateDbContextAsync(token);
        Assert.Empty(await db.McpOperations.ToArrayAsync(token));
    }

    private sealed class PausableDecisionGate : IOwnerMutationLock, IDisposable
    {
        public sealed record Checkpoint(TaskCompletionSource Entered, TaskCompletionSource Release);
        private readonly OwnerMutationLock _gate = new();
        private Checkpoint? _next;

        public Checkpoint PauseNext()
        {
            var checkpoint = new Checkpoint(new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Interlocked.CompareExchange(ref _next, checkpoint, null) is not null)
                throw new InvalidOperationException("A checkpoint is already pending.");
            return checkpoint;
        }

        public ValueTask<IAsyncDisposable> AcquireAsync(IEnumerable<Guid> ownerIds, CancellationToken cancellationToken = default) =>
            _gate.AcquireAsync(ownerIds, cancellationToken);

        public Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) => _gate.RunAsync(async () =>
        {
            if (Interlocked.Exchange(ref _next, null) is { } checkpoint)
            {
                checkpoint.Entered.TrySetResult();
                await checkpoint.Release.Task.WaitAsync(cancellationToken);
            }
            return await action();
        }, cancellationToken);

        public void Dispose() => _gate.Dispose();
    }
}

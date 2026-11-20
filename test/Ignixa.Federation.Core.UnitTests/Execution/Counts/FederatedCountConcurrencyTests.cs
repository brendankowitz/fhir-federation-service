using System.Diagnostics;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>Count calls run with bounded parallelism, and every started call is cancelled and joined before the count ends.</summary>
public sealed class FederatedCountConcurrencyTests
{
    [Fact]
    public async Task NeverRunsMoreCallsThanTheParallelismLimit()
    {
        SearchCapabilities capabilities = Certified(Fixture.Registry(5), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeShardClient();
        for (int ordinal = 0; ordinal < 5; ordinal++)
        {
            client.AnswerAfter(Fixture.Shard(ordinal), gate, ordinal + 1);
        }

        Task<int?> execution = Executor(client, capabilities, maxParallelCounts: 2).ExecuteAsync(plan, SearchEvery(capabilities), 5, Token);
        await client.WhenStarted(2).WaitAsync(Token);
        for (int yields = 0; yields < 10; yields++)
        {
            client.Calls.Count.ShouldBe(2);
            await Task.Yield();
        }

        gate.SetResult();

        (await execution).ShouldBe(15);
        client.Calls.Count.ShouldBe(5);
        client.MaxInFlight.ShouldBe(2);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("contract")]
    public async Task FailureCancelsAndJoinsEveryOtherCallBeforeThrowing(string exit)
    {
        SearchCapabilities capabilities = Certified(Fixture.Registry(3), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeShardClient();
        client.AnswerAfter(Fixture.Shard(0), new TaskCompletionSource(), 1);
        client.OnSearch(Fixture.Shard(1), Parked(cancelled, release));
        client.OnSearch(Fixture.Shard(2), async (_, _) =>
        {
            await fail.Task;
            return exit == "failed" ? throw new InvalidOperationException("backend said 500") : Count(null);
        });

        Task<int?> execution = Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 3, Token);
        await client.WhenStarted(3).WaitAsync(Token);
        fail.SetResult();
        await cancelled.Task.WaitAsync(Token);

        // s1's call is parked on release, so a joined count cannot have ended. Yielding gives an unjoined one time to end.
        for (int yields = 0; yields < 10; yields++)
        {
            execution.IsCompleted.ShouldBeFalse();
            await Task.Yield();
        }

        release.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);

        thrown.Shard.ShouldBe(Fixture.Shard(2));
        thrown.Reason.ShouldBe(exit == "failed" ? ShardFailureReason.Failed : ShardFailureReason.ContractViolation);
        client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
        client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task FailureStopsCallsThatHaveNotStarted()
    {
        SearchCapabilities capabilities = Certified(Fixture.Registry(3), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), (_, _) => Task.FromException<ShardPage>(new InvalidOperationException("backend said 500")));
        client.Answer(Fixture.Shard(1), 1);
        client.Answer(Fixture.Shard(2), 1);

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, capabilities, maxParallelCounts: 1).ExecuteAsync(plan, SearchEvery(capabilities), 3, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        client.Calls.Select(c => c.Shard).ShouldBe([Fixture.Shard(0)]);
    }

    [Fact]
    public async Task CallerCancellationSurfacesAsCancellationAfterJoiningEveryCall()
    {
        SearchCapabilities capabilities = Certified(Fixture.Registry(2), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeShardClient();
        client.AnswerAfter(Fixture.Shard(0), new TaskCompletionSource(), 1);
        client.OnSearch(Fixture.Shard(1), Parked(cancelled, release));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<int?> execution = Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, caller.Token);
        await client.WhenStarted(2).WaitAsync(Token);
        await caller.CancelAsync();
        await cancelled.Task.WaitAsync(Token);
        for (int yields = 0; yields < 10; yields++)
        {
            execution.IsCompleted.ShouldBeFalse();
            await Task.Yield();
        }

        release.SetResult();
        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);

        thrown.CancellationToken.ShouldBe(caller.Token);
        client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
        client.InFlight.ShouldBe(0);
    }

    /// <summary>A call that waits until cancelled, signals <paramref name="cancelled"/>, then holds until <paramref name="release"/>.</summary>
    private static Func<ShardSearchRequest, CancellationToken, Task<ShardPage>> Parked(TaskCompletionSource cancelled, TaskCompletionSource release) =>
        async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                await release.Task;
                throw;
            }

            throw new UnreachableException();
        };
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private const string CountBoom = "count boom";

    private const string CallbackBoom = "cancellation callback boom";

    private static readonly string[] Exits = ["failed", "contract"];

    /// <summary>Runs each timing-sensitive test 20 times.</summary>
    public static TheoryData<int> Iterations => [.. Enumerable.Range(1, 20)];

    /// <summary>A shard call failing and a shard answering with something other than a bare total, each 20 times.</summary>
    public static TheoryData<string, int> ExitsTimesIterations =>
        [.. from exit in Exits from iteration in Enumerable.Range(1, 20) select (exit, iteration)];

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task NeverRunsMoreCallsThanTheParallelismLimit(int iteration)
    {
        _ = iteration;
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
    [MemberData(nameof(ExitsTimesIterations))]
    public async Task FailureCancelsAndJoinsEveryOtherCallBeforeThrowing(string exit, int iteration)
    {
        _ = iteration;
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

    /// <summary>
    /// With one slot, s1 and s2 queue for it while s0 runs. s0 fails after yielding, so its failure releases the slot to a
    /// waiter that is already queued; that waiter must not start its call.
    /// </summary>
    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task SlotFreedByAFailureStartsNoQueuedCall(int iteration)
    {
        _ = iteration;
        SearchCapabilities capabilities = Certified(Fixture.Registry(3), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("backend said 500");
        });
        client.Answer(Fixture.Shard(1), 1);
        client.Answer(Fixture.Shard(2), 1);

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, capabilities, maxParallelCounts: 1).ExecuteAsync(plan, SearchEvery(capabilities), 3, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        client.Calls.Select(c => c.Shard).ShouldBe([Fixture.Shard(0)]);
    }

    /// <summary>s1's client throws before returning a task, while s0's call is running; s0 is cancelled and joined.</summary>
    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task ClientThatThrowsSynchronouslyFailsTheCountAfterJoiningEveryOtherCall(int iteration)
    {
        _ = iteration;
        SearchCapabilities capabilities = Certified(Fixture.Registry(2), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), Parked(cancelled, release));
        client.OnSearchThrowsSynchronously(1);

        Task<int?> execution = Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);
        await cancelled.Task.WaitAsync(Token);
        for (int yields = 0; yields < 10; yields++)
        {
            execution.IsCompleted.ShouldBeFalse();
            await Task.Yield();
        }

        release.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        thrown.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe(FakeShardClient.SynchronousFailureMessage);
        client.Calls.Select(c => c.Shard).ShouldBe([Fixture.Shard(0)]);
        client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
        client.InFlight.ShouldBe(0);
    }

    /// <summary>
    /// s0's cancellation callback throws, and its call then stays parked until the test releases it. The count must still
    /// join the call before it ends, and throw s1's own failure rather than the callback's exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExitsTimesIterations))]
    public async Task CancellationCallbackThatThrowsIsContainedAndTheCallIsJoinedBeforeTheCountEnds(string exit, int iteration)
    {
        _ = iteration;
        SearchCapabilities capabilities = Certified(Fixture.Registry(2), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var needed = new InvalidOperationException("backend said 500");
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), ThrowingCallbackThenParked(registered, callbackRan, release));
        client.OnSearch(Fixture.Shard(1), async (_, _) =>
        {
            await fail.Task;
            return exit == "failed" ? throw needed : Count(null);
        });

        Task<int?> execution = Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);
        await registered.Task.WaitAsync(Token);
        fail.SetResult();
        await callbackRan.Task.WaitAsync(Token);

        // s0's call is parked on release, so a joined count cannot have ended. Yielding gives an unjoined one time to end.
        for (int yields = 0; yields < 10; yields++)
        {
            execution.IsCompleted.ShouldBeFalse();
            await Task.Yield();
        }

        release.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        if (exit == "failed")
        {
            thrown.Reason.ShouldBe(ShardFailureReason.Failed);
            thrown.InnerException.ShouldBeSameAs(needed);
        }
        else
        {
            thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
            thrown.InnerException.ShouldBeNull();
        }

        client.Calls.Count.ShouldBe(2);
        client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
        client.InFlight.ShouldBe(0);
    }

    /// <summary>
    /// Every exception a failed count discards, a faulted call, a synchronous client throw and a throwing cancellation
    /// callback, must be observed. The count runs in a helper so nothing the test holds roots the discarded tasks, and
    /// their finalizers can report them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task ExceptionsAFailedCountDiscardsAreObserved(int iteration)
    {
        _ = iteration;
        var unobserved = new List<Exception>();
        void Record(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (Chain(args.Exception).Any(e => e.Message is CountBoom or CallbackBoom or FakeShardClient.SynchronousFailureMessage))
            {
                lock (unobserved)
                {
                    unobserved.Add(args.Exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += Record;
        try
        {
            (ShardId shard, ShardFailureReason reason) = await FailCountDiscardingThreeExceptionsAsync();

            shard.ShouldBe(Fixture.Shard(2));
            reason.ShouldBe(ShardFailureReason.Failed);
            for (int collections = 0; collections < 3; collections++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                unobserved.ShouldBeEmpty();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task CallerCancellationSurfacesAsCancellationAfterJoiningEveryCall(int iteration)
    {
        _ = iteration;
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

    /// <summary>
    /// Fails a count on s2's synchronous client throw while s0's cancellation callback throws and s1's call faults after
    /// the failure, and returns only the failure's shard and reason. Not inlined, so once it returns nothing on the test's
    /// frames roots the discarded tasks.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ShardId Shard, ShardFailureReason Reason)> FailCountDiscardingThreeExceptionsAsync()
    {
        SearchCapabilities capabilities = Certified(Fixture.Registry(3), "Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boom = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), ThrowingCallbackThenParked(registered, callbackRan, release));
        client.OnSearch(Fixture.Shard(1), async (_, _) =>
        {
            await boom.Task;
            throw new InvalidOperationException(CountBoom);
        });
        client.OnSearchThrowsSynchronously(2);

        Task<int?> execution = Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 3, Token);
        await callbackRan.Task.WaitAsync(Token);
        boom.SetResult();
        release.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);
        client.Calls.Count.ShouldBe(2);
        client.InFlight.ShouldBe(0);
        return (thrown.Shard, thrown.Reason);
    }

    /// <summary>An exception, its inner exceptions and, for an aggregate, every exception it holds.</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is { } single ? [single] : [];
        foreach (Exception nested in inner.SelectMany(Chain))
        {
            yield return nested;
        }
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

    /// <summary>
    /// A call whose cancellation callback signals <paramref name="callbackRan"/> and throws; the call holds until
    /// <paramref name="release"/>, then ends cancelled.
    /// </summary>
    private static Func<ShardSearchRequest, CancellationToken, Task<ShardPage>> ThrowingCallbackThenParked(
        TaskCompletionSource registered,
        TaskCompletionSource callbackRan,
        TaskCompletionSource release) =>
        async (_, cancellationToken) =>
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                callbackRan.SetResult();
                throw new InvalidOperationException(CallbackBoom);
            });
            registered.SetResult();
            await release.Task;
            throw new OperationCanceledException(cancellationToken);
        };
}
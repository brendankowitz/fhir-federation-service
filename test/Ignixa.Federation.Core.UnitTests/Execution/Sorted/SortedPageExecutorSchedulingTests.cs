using System.Runtime.CompilerServices;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>
/// How a sorted execution admits, joins and arbitrates shard calls (design Q7, Q9; tests 27–33). Gates order every
/// completion, so no outcome depends on timing.
/// </summary>
public sealed class SortedPageExecutorSchedulingTests
{
    private const LastUpdatedSort Ascending = LastUpdatedSort.Ascending;

    private const string DiscardedFailure = "A failure a lower ordinal's failure outranks.";

    /// <summary>Bounds a wait that only an executor admitting the wrong calls would never finish.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four gated owners are primed in ordinal batches of at most <paramref name="maxParallel"/>. Each batch is released
    /// against ordinal order; the next batch may start only once the whole batch has finished.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PrimingBatches_ReachButNeverExceedTheConcurrency_WhateverOrderTheyComplete(int maxParallel)
    {
        var client = new FakeShardClient();
        TaskCompletionSource[] gates = [.. Enumerable.Range(0, 4).Select(_ => Gate())];
        for (int i = 0; i < 4; i++)
        {
            client.OnSearch(i, gates[i], Fixture.Page(null, SortedFixture.Entry(i, "a", 10 - i), SortedFixture.Entry(i, "b", 20 - i)));
        }

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(4), maxParallel)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 3, 1, 0, 2), Ascending, 5, null, 10, Token);
        for (int started = 0; started < 4;)
        {
            int batch = Math.Min(maxParallel, 4 - started);
            await client.WhenStarted(started + batch).WaitAsync(Guard, Token);
            client.Calls.Count.ShouldBe(started + batch, "a later batch waits for this one");
            for (int i = started + batch - 1; i >= started; i--)
            {
                gates[i].SetResult();
                await client.Calls[i].Finished.WaitAsync(Token);
            }

            started += batch;
        }

        SortedPage page = await running;

        client.MaxInFlight.ShouldBe(maxParallel);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=5", "search s1 count=5", "search s2 count=5", "search s3 count=5"]);
        page.Ids().ShouldBe(["s3/a", "s2/a", "s1/a", "s0/a", "s3/b"]);
        page.Fetches.ShouldBe(4);
    }

    /// <summary>
    /// One batch holds s0, whose call goes wrong in the given way, s1, whose client fails, and s2, which the budget denies.
    /// s0's error is the one thrown, whichever of s0 and s1 completes first, and only once every started call has finished;
    /// s1's call is never cancelled because s0 went wrong.
    /// </summary>
    [Theory]
    [InlineData("malformed", true)]
    [InlineData("malformed", false)]
    [InlineData("anchor missing", true)]
    [InlineData("anchor missing", false)]
    [InlineData("failed", true)]
    [InlineData("failed", false)]
    [InlineData("timeout", true)]
    [InlineData("timeout", false)]
    public async Task FirstUnsuccessfulSlotByOrdinal_IsThrown_AfterTheBatchIsJoined(string kind, bool s0CompletesFirst)
    {
        var client = new FakeShardClient();
        TaskCompletionSource s0Gate = Gate(), s1Gate = Gate();
        Exception clientError = kind == "timeout" ? new OperationCanceledException() : new InvalidOperationException("s0 failed");
        client.OnSearch(Fixture.Shard(0), async (_, token) =>
        {
            await s0Gate.Task.WaitAsync(token);
            return kind switch
            {
                "malformed" => Fixture.Page(null, SortedFixture.Entry(0, "b", 2), SortedFixture.Entry(0, "a", 1)),
                "anchor missing" => Fixture.Page(null, SortedFixture.Entry(0, "b", 2)),
                _ => throw clientError,
            };
        });
        CancellationToken s1Token = default;
        client.OnSearch(Fixture.Shard(1), async (_, token) =>
        {
            s1Token = token;
            await s1Gate.Task.WaitAsync(token);
            throw new InvalidOperationException("s1 failed");
        });
        SortedContinuation? resumeFrom = kind != "anchor missing" ? null : new SortedContinuation(
            Fixture.Patient,
            Ascending,
            2,
            [
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 2), SortedFixture.Anchor(0, "a", 1)),
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, pageSize: 2), null),
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(2, pageSize: 2), null),
            ]);

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(3), 3)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, resumeFrom, 2, Token);
        await client.WhenStarted(2).WaitAsync(Guard, Token);
        foreach (int slot in s0CompletesFirst ? [0, 1] : new[] { 1, 0 })
        {
            running.IsCompleted.ShouldBeFalse("the batch is joined before any outcome is chosen");
            (slot == 0 ? s0Gate : s1Gate).SetResult();
            await client.Calls[slot].Finished.WaitAsync(Token);
        }

        Exception thrown = await Should.ThrowAsync<Exception>(running);

        switch (kind)
        {
            case "anchor missing":
                thrown.ShouldBeOfType<ContinuationAnchorMissingException>().Shard.ShouldBe(Fixture.Shard(0));
                break;
            default:
                var failure = thrown.ShouldBeOfType<ShardFailureException>();
                (failure.Shard, failure.Reason).ShouldBe((Fixture.Shard(0), kind switch
                {
                    "malformed" => ShardFailureReason.ContractViolation,
                    "failed" => ShardFailureReason.Failed,
                    _ => ShardFailureReason.Timeout,
                }));
                if (kind != "malformed")
                {
                    failure.InnerException.ShouldBeSameAs(clientError);
                }

                break;
        }

        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2"]);
        client.InFlight.ShouldBe(0);
        s1Token.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// When s0's failure wins, s1's failure and s2's budget denial are discarded but still observed, so neither surfaces
    /// later as an unobserved task exception. The page runs in a helper so nothing the test holds roots the discarded tasks.
    /// </summary>
    [Fact]
    public async Task DiscardedFailuresOfAFailedBatch_AreObserved()
    {
        var unobserved = new List<Exception>();
        void Record(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(e => e is FetchBudgetExhaustedException
                || e.InnerException?.Message == DiscardedFailure))
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
            ShardId failed = await FailABatchAsync();

            failed.ShouldBe(Fixture.Shard(0));
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ShardId> FailABatchAsync()
    {
        var client = new FakeShardClient();
        client.OnSearchThrows(0, new InvalidOperationException("s0 failed"));
        client.OnSearchThrows(1, new InvalidOperationException(DiscardedFailure));
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            SortedFixture.Executor(client, Fixture.Registry(3), 3).ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, null, 2, Token));
        return thrown.Shard;
    }

    /// <summary>
    /// The caller cancels while s1 is in flight and s0 has already failed; s2 was denied by the budget. Cancellation wins,
    /// reaches s1 through the token it was given, and is thrown only once s1, which delays its exit, has finished.
    /// </summary>
    [Fact]
    public async Task CallerCancellationMidBatch_BeatsFailureAndDenial_OnceTheBatchHasFinished()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var client = new FakeShardClient();
        TaskCompletionSource s1Cancelled = Gate(), s1Exit = Gate();
        client.OnSearchThrows(0, new InvalidOperationException("s0 failed"));
        CancellationToken s1Token = default;
        client.OnSearch(Fixture.Shard(1), async (_, token) =>
        {
            s1Token = token;
            try
            {
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Unreachable.");
            }
            catch (OperationCanceledException)
            {
                s1Cancelled.SetResult();
                await s1Exit.Task.WaitAsync(Token);
                throw;
            }
        });

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(3), 3)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, null, 2, caller.Token);
        await client.WhenStarted(2).WaitAsync(Guard, Token);
        await caller.CancelAsync();
        await s1Cancelled.Task.WaitAsync(Guard, Token);
        running.IsCompleted.ShouldBeFalse("s1 has not finished");
        s1Exit.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(running);

        s1Token.ShouldBe(caller.Token);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2"]);
        client.InFlight.ShouldBe(0);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

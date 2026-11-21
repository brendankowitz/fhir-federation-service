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
    /// Four owners are primed in fixed ordinal batches of at most <paramref name="maxParallel"/>. The last call of each
    /// batch completes as it starts and the rest are released against ordinal order, lowest ordinal last, so a free slot
    /// opens while the batch still runs. Every call records how many calls had finished when it started: only the whole
    /// of the earlier batches, so no call slides into a slot its batch freed early.
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
        int[] finishedAtStart = new int[4];
        for (int i = 0; i < 4; i++)
        {
            int ordinal = i;
            bool lastOfBatch = i % maxParallel == maxParallel - 1 || i == 3;
            ShardPage response = Fixture.Page(null, SortedFixture.Entry(i, "a", 10 - i), SortedFixture.Entry(i, "b", 20 - i));
            client.OnSearch(Fixture.Shard(i), (_, cancellationToken) =>
            {
                finishedAtStart[ordinal] = client.Finished.Count;
                return lastOfBatch ? Task.FromResult(response) : Respond(gates[ordinal], response, cancellationToken);
            });
        }

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(4), maxParallel)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 3, 1, 0, 2), Ascending, 5, null, 10, Token);
        for (int started = 0; started < 4;)
        {
            int batch = Math.Min(maxParallel, 4 - started);
            await client.WhenStarted(started + batch).WaitAsync(Guard, Token);
            if (batch > 1)
            {
                client.Calls.Count.ShouldBe(started + batch, "a later batch waits for this one's gated calls");
            }
            for (int i = started + batch - 2; i >= started; i--)
            {
                gates[i].SetResult();
                await client.Calls[i].Finished.WaitAsync(Guard, Token);
            }

            started += batch;
        }

        SortedPage page = await running.WaitAsync(Guard, Token);

        finishedAtStart.ShouldBe([.. Enumerable.Range(0, 4).Select(i => i / maxParallel * maxParallel)], "a call starts only after every earlier batch");
        client.MaxInFlight.ShouldBe(maxParallel);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=5", "search s1 count=5", "search s2 count=5", "search s3 count=5"]);
        page.Ids().ShouldBe(["s3/a", "s2/a", "s1/a", "s0/a", "s3/b"]);
        page.Fetches.ShouldBe(4);
    }

    /// <summary>
    /// Redemption primes in the same batches as a first page: the replayed cursor of s0 and the saved search of s1 run
    /// together, the exhausted s2 makes no call, and releasing s1 first changes nothing.
    /// </summary>
    [Fact]
    public async Task RedemptionPriming_RunsInTheSameBoundedBatches()
    {
        var client = new FakeShardClient();
        TaskCompletionSource s0Gate = Gate(), s1Gate = Gate();
        ShardCursor p1 = Fixture.Cursor(0, "p1");
        client.OnContinue(p1, s0Gate, Fixture.Page(null, SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "c", 3)));
        client.OnSearch(1, s1Gate, Fixture.Page(null, SortedFixture.Entry(1, "b", 2)));
        var resumeFrom = new SortedContinuation(
            Fixture.Patient,
            Ascending,
            2,
            [
                SortedStreamContinuation.AtCursor(p1, SortedFixture.Anchor(0, "a", 1), new CursorCycleState(1UL, 2, 0), null),
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, pageSize: 2), null),
                SortedStreamContinuation.AtEnd(Fixture.Shard(2), SortedFixture.Anchor(2, "z", 0)),
            ]);

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(3), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, resumeFrom, 10, Token);
        await client.WhenStarted(2).WaitAsync(Guard, Token);
        s1Gate.SetResult();
        await client.Calls[1].Finished.WaitAsync(Guard, Token);
        s0Gate.SetResult();
        SortedPage page = await running.WaitAsync(Guard, Token);

        client.MaxInFlight.ShouldBe(2);
        client.Calls.Select(c => c.ToString()).ShouldBe(["continue s0", "search s1 count=2"]);
        page.Ids().ShouldBe(["s1/b", "s0/c"]);
        page.Fetches.ShouldBe(2);
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
            await client.Calls[slot].Finished.WaitAsync(Guard, Token);
        }

        Exception thrown = await Should.ThrowAsync<Exception>(running.WaitAsync(Guard, Token));

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
                await s1Exit.Task.WaitAsync(Guard, Token);
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

        await Should.ThrowAsync<OperationCanceledException>(running.WaitAsync(Guard, Token));

        s1Token.ShouldBe(caller.Token);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2"]);
        client.InFlight.ShouldBe(0);
    }

    /// <summary>Cancellation observed between two starts of a batch admits no further call.</summary>
    [Fact]
    public async Task CallerCancellationDuringAdmission_StartsNoFurtherCall()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var client = new FakeShardClient();
        client.OnSearch(Fixture.Shard(0), async (_, _) =>
        {
            await caller.CancelAsync();
            return Fixture.Page(null, SortedFixture.Entry(0, "a", 1));
        });
        client.OnSearch(1, Fixture.Page(null, SortedFixture.Entry(1, "b", 2)));
        client.OnSearch(2, Fixture.Page(null, SortedFixture.Entry(2, "c", 3)));

        await Should.ThrowAsync<OperationCanceledException>(SortedFixture.Executor(client, Fixture.Registry(3), 3)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, null, 10, caller.Token));

        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2"]);
    }

    /// <summary>
    /// A client that throws before returning its task fails like one whose task faults: as <see cref="ShardFailureReason.Failed"/>
    /// with the shard and cause, and only once the rest of its batch has finished.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NeededClientFailure_IsFailedWithItsShardAndCause_OnceItsBatchHasFinished(bool synchronous)
    {
        var client = new FakeShardClient();
        var cause = new InvalidOperationException("s0 failed");
        if (synchronous)
        {
            client.OnSearchThrowsSynchronously(0);
        }
        else
        {
            client.OnSearchThrows(0, cause);
        }

        TaskCompletionSource s1Gate = Gate();
        client.OnSearch(1, s1Gate, Fixture.Page(null, SortedFixture.Entry(1, "b", 2)));

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(2), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1), Ascending, 2, null, 10, Token);
        await client.WhenStarted(synchronous ? 1 : 2).WaitAsync(Guard, Token);
        running.IsCompleted.ShouldBeFalse("s1 has not finished");
        s1Gate.SetResult();
        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(running.WaitAsync(Guard, Token));

        (failure.Shard, failure.Reason).ShouldBe((Fixture.Shard(0), ShardFailureReason.Failed));
        if (synchronous)
        {
            failure.InnerException.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe(FakeShardClient.SynchronousFailureMessage);
        }
        else
        {
            failure.InnerException.ShouldBeSameAs(cause);
        }

        client.InFlight.ShouldBe(0);
    }

    /// <summary>A refill that fails after its owner's match was tentatively emitted fails the whole page.</summary>
    [Fact]
    public async Task FailedRefillAfterATentativeMatch_FailsThePage()
    {
        var client = new FakeShardClient();
        var cause = new InvalidOperationException("s0 refill failed");
        client.OnSearch(0, Fixture.Page(Fixture.Cursor(0, "p1"), SortedFixture.Entry(0, "a", 1)));
        client.OnContinue(Fixture.Cursor(0, "p1"), _ => Task.FromException<ShardPage>(cause));
        client.OnSearch(1, Fixture.Page(null, SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(SortedFixture.Executor(client, Fixture.Registry(2), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1), Ascending, 3, null, 10, Token));

        (failure.Shard, failure.Reason, failure.InnerException).ShouldBe((Fixture.Shard(0), ShardFailureReason.Failed, cause));
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=3", "search s1 count=3", "continue s0"]);
    }

    /// <summary>
    /// A client that reports cancellation while the caller is live has timed out; the caller did not cancel, so neither
    /// the execution nor a sibling sees cancellation.
    /// </summary>
    [Fact]
    public async Task ClientCancellationWhileTheCallerIsLive_IsATimeout()
    {
        var client = new FakeShardClient();
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();
        var timedOut = new OperationCanceledException(expired.Token);
        client.OnSearchThrows(1, timedOut);
        CancellationToken s0Token = default;
        client.OnSearch(Fixture.Shard(0), (_, token) =>
        {
            s0Token = token;
            return Task.FromResult(Fixture.Page(null, SortedFixture.Entry(0, "a", 1)));
        });

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(SortedFixture.Executor(client, Fixture.Registry(2), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1), Ascending, 2, null, 10, Token));

        (failure.Shard, failure.Reason, failure.InnerException).ShouldBe((Fixture.Shard(1), ShardFailureReason.Timeout, timedOut));
        s0Token.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// A budget below the three owners' head demand admits only its ordinal prefix in one batch: those calls start and
    /// finish, the denied owners start nothing, and the page fails. A budget of 0 starts nothing at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BudgetBelowHeadDemand_AdmitsOnlyItsOrdinalPrefix_AndFails(int budget)
    {
        var client = new FakeShardClient();
        for (int i = 0; i < 3; i++)
        {
            client.OnSearch(i, Fixture.Page(null, SortedFixture.Entry(i, "a", i + 1)));
        }

        await Should.ThrowAsync<FetchBudgetExhaustedException>(SortedFixture.Executor(client, Fixture.Registry(3), 3)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, null, budget, Token));

        client.Calls.Select(c => c.ToString()).ShouldBe(Enumerable.Range(0, budget).Select(i => $"search s{i} count=2"));
        client.InFlight.ShouldBe(0);
    }

    /// <summary>A budget that covers exactly the calls the page needs, refill included, succeeds and reports them.</summary>
    [Fact]
    public async Task ExactBudget_Succeeds_ReportingTheCallsStarted()
    {
        FakeShardClient client = RefillNeededForTheThirdMatch();

        SortedPage page = await SortedFixture.Executor(client, Fixture.Registry(2), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1), Ascending, 3, null, 3, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b", "s0/c"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=3", "search s1 count=3", "continue s0"]);
        page.Fetches.ShouldBe(3);
    }

    /// <summary>
    /// The third match needs s0's refill, which a budget of 2 denies; the two matches already chosen are not returned.
    /// </summary>
    [Fact]
    public async Task RequiredRefillBeyondTheBudget_FailsTheWholePage()
    {
        FakeShardClient client = RefillNeededForTheThirdMatch();

        await Should.ThrowAsync<FetchBudgetExhaustedException>(SortedFixture.Executor(client, Fixture.Registry(2), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1), Ascending, 3, null, 2, Token));

        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=3", "search s1 count=3"]);
    }

    /// <summary>
    /// s0 fails in the first batch of two: its batch-mate s1 still runs to completion, but the batch holding s2 never
    /// starts.
    /// </summary>
    [Fact]
    public async Task FailedBatch_StartsNoLaterBatch()
    {
        var client = new FakeShardClient();
        client.OnSearchThrows(0, new InvalidOperationException("s0 failed"));
        client.OnSearch(1, Fixture.Page(null, SortedFixture.Entry(1, "b", 2)));
        client.OnSearch(2, Fixture.Page(null, SortedFixture.Entry(2, "c", 3)));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(SortedFixture.Executor(client, Fixture.Registry(3), 2)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 0, 1, 2), Ascending, 2, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2"]);
    }

    /// <summary>s0: [a@1] → p1 [c@3] → p2 [e@5]; s1: [b@2, d@4]. A page of 3 needs s0's p1 but not its p2.</summary>
    private static FakeShardClient RefillNeededForTheThirdMatch()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "c", 3)], [SortedFixture.Entry(0, "e", 5)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "d", 4)]);
        return client;
    }

    private static async Task<ShardPage> Respond(TaskCompletionSource gate, ShardPage page, CancellationToken cancellationToken)
    {
        await gate.Task.WaitAsync(cancellationToken);
        return page;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

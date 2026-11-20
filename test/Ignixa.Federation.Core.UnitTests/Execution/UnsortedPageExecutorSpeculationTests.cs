using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Selective speculative fanout, ordered emission over it, and joining it on every exit (design spec §7.1).</summary>
public sealed class UnsortedPageExecutorSpeculationTests
{
    /// <summary>Runs each timing-sensitive test 20 times.</summary>
    public static TheoryData<int> Iterations => [.. Enumerable.Range(1, 20)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstResponseFillsThePage_NoLaterShardIsCalledEvenWithFreeSlots()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a", "b")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), 4).ExecuteAsync(Fixture.Streams(3), 2, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b"));
        client.Calls.ShouldHaveSingleItem().Shard.ShouldBe(Fixture.Shard(0));
        page.Fetches.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task UnderfilledFirstResponse_FetchesEachLaterShardOnceWithTheCountRemainingAtThatTime(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.Serve(1, ["b", "c"]);
        client.Serve(2, ["d", "e", "f", "g"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), 4).ExecuteAsync(Fixture.Streams(3), 5, null, 10, Token);

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c"), .. Fixture.Ids(2, "d", "e")]);
        client.Calls.Select(c => c.ToString()).Order().ShouldBe(["search s0 count=5", "search s1 count=4", "search s2 count=4"]);
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Search.ShouldBeSameAs(client.Calls.Single(c => c.Shard == Fixture.Shard(2)).Search);
        next.LastEmitted.ShouldBe(Fixture.Key(2, "e"));
        page.Fetches.ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task LaterShardCompletesFirst_EmissionStillFollowsOrdinalOrder(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.OnSearch(1, first, Fixture.Page(null, Fixture.Entries(1, "b")));
        client.OnSearch(2, second, Fixture.Page(null, Fixture.Entries(2, "c")));

        Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(3), 3).ExecuteAsync(Fixture.Streams(3), 10, null, 10, Token);
        await client.WhenStarted(3);
        second.SetResult();
        await client.Calls.Single(c => c.Shard == Fixture.Shard(2)).Finished;
        first.SetResult();
        UnsortedPage page = await execution;

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b"), .. Fixture.Ids(2, "c")]);
        client.Finished.Select(c => c.Shard).ShouldBe([Fixture.Shard(0), Fixture.Shard(2), Fixture.Shard(1)]);
        page.Next.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task InFlightCalls_NeverExceedMaxParallelFetches(int iteration)
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        var gates = Enumerable.Range(0, 6).Select(_ => new TaskCompletionSource()).ToArray();
        client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a")));
        client.OnContinue(cursor, gates[0], Fixture.Page(null, Fixture.Entries(0, "b")));
        for (int shard = 1; shard < 6; shard++)
        {
            client.OnSearch(shard, gates[shard], Fixture.Page(null, Fixture.Entries(shard, "x")));
        }

        Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(6), 3).ExecuteAsync(Fixture.Streams(6), 100, null, 100, Token);
        await client.WhenStarted(4);
        client.InFlight.ShouldBe(3);
        foreach (TaskCompletionSource gate in iteration % 2 == 0 ? gates : gates.Reverse())
        {
            gate.SetResult();
        }

        UnsortedPage page = await execution;

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a", "b"), .. Enumerable.Range(1, 5).Select(shard => $"s{shard}/x")]);
        client.MaxInFlight.ShouldBe(3);
        client.Calls.Count.ShouldBe(7);
        page.Fetches.ShouldBe(7);
    }

    [Fact]
    public async Task OneFetchSlot_FetchesShardsOneAtATimeWithTheCountRemainingThen()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.Serve(1, ["b", "c"]);
        client.Serve(2, ["d", "e", "f"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), 1).ExecuteAsync(Fixture.Streams(3), 5, null, 10, Token);

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c"), .. Fixture.Ids(2, "d", "e")]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=5", "search s1 count=4", "search s2 count=2"]);
        client.MaxInFlight.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task CallerCancels_PropagatesOperationCanceledAfterEveryStartedCallFinished(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        var never = new TaskCompletionSource();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a")));
        client.OnContinue(cursor, never, Fixture.Page(null));
        for (int shard = 1; shard < 4; shard++)
        {
            client.OnSearch(shard, never, Fixture.Page(null, Fixture.Entries(shard, "x")));
        }

        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(4), 3).ExecuteAsync(Fixture.Streams(4), 10, null, 10, caller.Token);
        await client.WhenStarted(4);
        await caller.CancelAsync();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        client.InFlight.ShouldBe(0);
        client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task SpeculativeFailureOfAShardThePageNeverReaches_IsDiscarded(int iteration)
    {
        _ = iteration;
        var unobserved = new List<Exception>();
        void Record(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(e => e.Message.Contains("speculative boom", StringComparison.Ordinal)))
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
            var client = new FakeShardClient();
            var gate = new TaskCompletionSource();
            ShardCursor cursor = Fixture.Cursor(0, "1");
            client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a")));
            client.OnContinue(cursor, gate, Fixture.Page(null, Fixture.Entries(0, "b", "c")));
            client.OnSearchThrows(1, new InvalidOperationException("speculative boom"));
            client.OnSearchThrowsSynchronously(2);

            Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(3), 3).ExecuteAsync(Fixture.Streams(3), 3, null, 10, Token);
            await client.WhenStarted(3);
            await client.Calls.Single(c => c.Shard == Fixture.Shard(1)).Finished;
            gate.SetResult();
            UnsortedPage page = await execution;

            page.Ids().ShouldBe(Fixture.Ids(0, "a", "b", "c"));
            UnsortedContinuation next = page.Next.ShouldNotBeNull();
            next.Shard.ShouldBe(Fixture.Shard(1));
            next.Search.ShouldBeNull();
            next.Cursor.ShouldBeNull();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            unobserved.ShouldBeEmpty();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Record;
        }
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task SpeculativeFailureOfAShardThePageNeeds_FailsThePage(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        var failure = new InvalidOperationException("backend said 500");
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.OnSearchThrows(1, failure);
        client.OnSearch(2, Fixture.Page(null, Fixture.Entries(2, "c")));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(3), 3).ExecuteAsync(Fixture.Streams(3), 10, null, 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        thrown.InnerException.ShouldBeSameAs(failure);
        client.InFlight.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task SpeculativeTimeoutOfAShardThePageNeeds_IsAShardTimeout(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.OnSearchThrows(1, new TaskCanceledException("backend call timed out"));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(2), 2).ExecuteAsync(Fixture.Streams(2), 10, null, 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        thrown.Reason.ShouldBe(ShardFailureReason.Timeout);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task MalformedSpeculativePageOfAShardThePageNeverReaches_IsDiscarded(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.OnSearch(1, Fixture.Page(null, Fixture.Entries(1, "b", "c")));
        client.OnSearch(2, Fixture.Page(null, Fixture.Entries(2, "x", "x")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), 3).ExecuteAsync(Fixture.Streams(3), 3, null, 10, Token);

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c")]);
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Shard.ShouldBe(Fixture.Shard(2));
        next.Search.ShouldBeNull();
        next.Cursor.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task ChainingPagesOverSpeculativeStreams_ReproducesTheFullResultAndReplaysTheSpeculativeCount(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);
        client.Serve(1, ["b", "c", "d", "e", "f", "g"], overReturn: 1);
        client.Serve(2, ["x", "y", "z"]);
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(3), 4);

        UnsortedPage first = await executor.ExecuteAsync(Fixture.Streams(3), 3, null, 100, Token);
        string[] chained = await UnsortedPageExecutorContinuationTests.ChainAsync(executor, Fixture.Streams(3), 3);

        first.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c")]);
        UnsortedContinuation next = first.Next.ShouldNotBeNull();
        next.Search.ShouldNotBeNull().PageSize.ShouldBe(2);
        next.LastEmitted.ShouldBe(Fixture.Key(1, "c"));
        chained.ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c", "d", "e", "f", "g"), .. Fixture.Ids(2, "x", "y", "z")]);
    }
}

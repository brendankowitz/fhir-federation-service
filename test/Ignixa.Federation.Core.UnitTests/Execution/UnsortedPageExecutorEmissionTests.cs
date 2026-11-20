using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Emission order and the shape of <see cref="UnsortedPage.Next"/> on the sequential (frontier) path (design spec §7.1).</summary>
public sealed class UnsortedPageExecutorEmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstShardFillsThePage_NoOtherShardIsCalled()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b", "c", "d", "e"]);
        client.Serve(1, ["x"]);
        client.Serve(2, ["y"]);
        ShardSearchRequest[] streams = Fixture.Streams(3);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), maxParallelFetches: 4)
            .ExecuteAsync(streams, 3, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b", "c"));
        page.Fetches.ShouldBe(1);
        ShardSearchRequest sent = client.Calls.ShouldHaveSingleItem().Search.ShouldNotBeNull();
        sent.Shard.ShouldBe(Fixture.Shard(0));
        sent.PageSize.ShouldBe(3);
        sent.Type.ShouldBe(streams[0].Type);
        sent.Parameters.ShouldBe(streams[0].Parameters);
        sent.Method.ShouldBe(streams[0].Method);
    }

    [Fact]
    public async Task PageEndsAtABackendPageWithNext_NextIsThatCursorWithoutAnAnchor()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a", "b")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 2, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b"));
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Shard.ShouldBe(Fixture.Shard(0));
        next.Cursor.ShouldBe(cursor);
        next.Search.ShouldBeNull();
        next.LastEmitted.ShouldBeNull();
    }

    [Fact]
    public async Task ShortAndEmptyInteriorPages_AreFollowedViaNext()
    {
        var client = new FakeShardClient();
        ShardCursor first = Fixture.Cursor(0, "1");
        ShardCursor second = Fixture.Cursor(0, "2");
        client.OnSearch(0, Fixture.Page(first, Fixture.Entries(0, "a")));
        client.OnContinue(first, Fixture.Page(second));
        client.OnContinue(second, Fixture.Page(null, Fixture.Entries(0, "b", "c")));
        client.Serve(1, ["x", "y"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 4, null, 10, Token);

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a", "b", "c"), .. Fixture.Ids(1, "x")]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=4", "continue s0", "continue s0", "search s1 count=1"]);
        client.Calls[1].Cursor.ShouldBe(first);
        client.Calls[2].Cursor.ShouldBe(second);
        page.Fetches.ShouldBe(4);
    }

    [Fact]
    public async Task PageFillsExactlyAtTheEndOfAnExhaustedShard_NextStartsTheFollowingShard()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3)).ExecuteAsync(Fixture.Streams(3), 2, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b"));
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Shard.ShouldBe(Fixture.Shard(1));
        next.Search.ShouldBeNull();
        next.Cursor.ShouldBeNull();
        next.LastEmitted.ShouldBeNull();
        client.Calls.ShouldAllBe(c => c.Shard == Fixture.Shard(0));
    }

    [Fact]
    public async Task EveryShardExhausted_NextIsNull()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);
        client.Serve(1, ["b"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 5, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        page.Next.ShouldBeNull();
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=5", "search s1 count=4"]);
    }

    [Fact]
    public async Task PageFillsExactlyAtTheEndOfTheLastShard_NextIsNull()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);
        client.Serve(1, ["b"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 2, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        page.Next.ShouldBeNull();
    }

    [Fact]
    public async Task SentCount_IsTheRemainingPageSizeCappedByTheShardsMaxCount()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b", "c"], profile: Fixture.SmallPages);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2, smallPageShards: 0))
            .ExecuteAsync(Fixture.Streams(2), 3, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b", "c"));
        client.Calls[0].Search.ShouldNotBeNull().PageSize.ShouldBe(Fixture.SmallPages.MaxCount);
    }

    [Fact]
    public async Task Streams_AreEmittedInRegistryOrdinalOrder_NotInputOrder()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);
        client.Serve(1, ["b"]);
        client.Serve(2, ["c"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3))
            .ExecuteAsync([.. Fixture.Streams(3).Reverse()], 3, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b", "s2/c"]);
    }

    [Fact]
    public async Task SameTypeAndIdOnDistinctShards_AreBothEmitted()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["p1"]);
        client.Serve(1, ["p1"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 5, null, 10, Token);

        page.Ids().ShouldBe(["s0/p1", "s1/p1"]);
        page.Matches.Select(m => m.Key).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task ResumeAtStart_FetchesFromThatShardAndNeverAnEarlierOne()
    {
        var client = new FakeShardClient();
        client.Serve(1, ["x"]);
        client.Serve(2, ["y"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3))
            .ExecuteAsync(Fixture.Streams(3), 5, UnsortedContinuation.AtStart(Fixture.Shard(1)), 10, Token);

        page.Ids().ShouldBe(["s1/x", "s2/y"]);
        client.Calls.ShouldNotContain(c => c.Shard == Fixture.Shard(0));
    }

    [Fact]
    public async Task ResumeAtCursor_RedeemsTheCursorAndNeverFetchesAnEarlierShard()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(1, "7");
        client.OnContinue(cursor, Fixture.Page(null, Fixture.Entries(1, "y", "z")));
        client.Serve(2, ["w"]);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3))
            .ExecuteAsync(Fixture.Streams(3), 5, UnsortedContinuation.AtCursor(cursor, null, CursorCycleState.Initial), 10, Token);

        page.Ids().ShouldBe(["s1/y", "s1/z", "s2/w"]);
        client.Calls[0].Cursor.ShouldBe(cursor);
        client.Calls.ShouldNotContain(c => c.Shard == Fixture.Shard(0));
    }
}

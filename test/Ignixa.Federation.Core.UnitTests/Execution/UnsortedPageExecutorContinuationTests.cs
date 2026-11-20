using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Continuation by re-execution and key-based skip (design spec §7.4).</summary>
public sealed class UnsortedPageExecutorContinuationTests
{
    private static readonly string[] Letters = ["a", "b", "c", "d", "e", "f", "g"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PageFillsInsideAnOverReturnedBackendPage_NextReplaysTheSentRequestWithTheLastEmittedKey()
    {
        var client = new FakeShardClient();
        client.Serve(0, Letters, overReturn: 4);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 3, null, 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "a", "b", "c"));
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Shard.ShouldBe(Fixture.Shard(0));
        next.Search.ShouldBeSameAs(client.Calls.ShouldHaveSingleItem().Search);
        next.Cursor.ShouldBeNull();
        next.LastEmitted.ShouldBe(Fixture.Key(0, "c"));
    }

    [Fact]
    public async Task ResumingTwiceOnIdenticalData_GivesIdenticalPages()
    {
        var client = new FakeShardClient();
        client.Serve(0, Letters, overReturn: 4);
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(2));
        UnsortedContinuation next = (await executor.ExecuteAsync(Fixture.Streams(2), 3, null, 10, Token)).Next.ShouldNotBeNull();

        UnsortedPage first = await executor.ExecuteAsync(Fixture.Streams(2), 3, next, 10, Token);
        UnsortedPage second = await executor.ExecuteAsync(Fixture.Streams(2), 3, next, 10, Token);

        first.Ids().ShouldBe(Fixture.Ids(0, "d", "e", "f"));
        second.Ids().ShouldBe(first.Ids());
        client.Calls[1].Search.ShouldBeSameAs(next.Search);
        client.Calls[2].Search.ShouldBeSameAs(next.Search);
        foreach (UnsortedPage page in new[] { first, second })
        {
            UnsortedContinuation following = page.Next.ShouldNotBeNull();
            following.Search.ShouldBeSameAs(next.Search);
            following.LastEmitted.ShouldBe(Fixture.Key(0, "f"));
            page.Fetches.ShouldBe(1);
        }
    }

    [Fact]
    public async Task ResumeAtCursorWithAnAnchor_SkipsThroughTheAnchorOnTheReplayedPage()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnContinue(cursor, Fixture.Page(null, Fixture.Entries(0, "a", "b", "c", "d")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(1))
            .ExecuteAsync(Fixture.Streams(1), 1, UnsortedContinuation.AtCursor(cursor, Fixture.Key(0, "b")), 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "c"));
        UnsortedContinuation next = page.Next.ShouldNotBeNull();
        next.Cursor.ShouldBe(cursor);
        next.LastEmitted.ShouldBe(Fixture.Key(0, "c"));
    }

    [Fact]
    public async Task AnchorIsTheLastEntryOfTheReplayedPage_ContinuesWithTheFollowingPage()
    {
        var client = new FakeShardClient();
        ShardCursor first = Fixture.Cursor(0, "1");
        ShardCursor second = Fixture.Cursor(0, "2");
        client.OnContinue(first, Fixture.Page(second, Fixture.Entries(0, "a", "b")));
        client.OnContinue(second, Fixture.Page(null, Fixture.Entries(0, "c")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(1))
            .ExecuteAsync(Fixture.Streams(1), 5, UnsortedContinuation.AtCursor(first, Fixture.Key(0, "b")), 10, Token);

        page.Ids().ShouldBe(Fixture.Ids(0, "c"));
        page.Next.ShouldBeNull();
    }

    [Fact]
    public async Task AnchorMissingFromTheReplayedSearch_ThrowsContinuationAnchorMissing()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a", "b")));
        var sent = new ShardSearchRequest(Fixture.Shard(0), Fixture.Patient, [new ParameterOccurrence("name", Fixture.SecretValue)], 3, ShardSearchMethod.Get);

        ContinuationAnchorMissingException thrown = await Should.ThrowAsync<ContinuationAnchorMissingException>(
            Fixture.Executor(client, Fixture.Registry(2))
                .ExecuteAsync(Fixture.Streams(2), 2, UnsortedContinuation.AtSearch(sent, Fixture.Key(0, "gone")), 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Message.ShouldNotContain("gone");
    }

    [Fact]
    public async Task AnchorMissingFromTheReplayedCursorPage_ThrowsContinuationAnchorMissing()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(1, "1");
        client.OnContinue(cursor, Fixture.Page(null, Fixture.Entries(1, "a")));

        ContinuationAnchorMissingException thrown = await Should.ThrowAsync<ContinuationAnchorMissingException>(
            Fixture.Executor(client, Fixture.Registry(2))
                .ExecuteAsync(Fixture.Streams(2), 2, UnsortedContinuation.AtCursor(cursor, Fixture.Key(1, "gone")), 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        thrown.Message.ShouldNotContain(cursor.Target);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ChainingPages_ReproducesTheFullResultWithNoSkipOrDuplicate(int pageSize)
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b", "c", "d", "e"], overReturn: 1);
        client.Serve(1, []);
        client.Serve(2, ["x", "y", "z", "w"]);
        string[] expected = [.. Fixture.Ids(0, "a", "b", "c", "d", "e"), .. Fixture.Ids(2, "x", "y", "z", "w")];

        string[] chained = await ChainAsync(Fixture.Executor(client, Fixture.Registry(3)), Fixture.Streams(3), pageSize);

        chained.ShouldBe(expected);
    }

    internal static async Task<string[]> ChainAsync(UnsortedPageExecutor executor, ShardSearchRequest[] streams, int pageSize)
    {
        var all = new List<string>();
        UnsortedContinuation? next = null;
        for (int step = 0; step < 50; step++)
        {
            UnsortedPage page = await executor.ExecuteAsync(streams, pageSize, next, 100, Token);
            page.Matches.Count.ShouldBeLessThanOrEqualTo(pageSize);
            all.AddRange(page.Ids());
            next = page.Next;
            if (next is null)
            {
                return [.. all];
            }
        }

        throw new InvalidOperationException("The chain did not end.");
    }
}

using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>
/// What each owner's checkpoint saves at the page cut, and how redeeming it re-executes the search (design Q2, Q3 and
/// algorithm steps 1 to 8).
/// </summary>
public sealed class SortedPageExecutorReplayTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Redeem_ReplaysEachSavedSearchVerbatim_AndContinuesAfterItsAnchor()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "c", 3), SortedFixture.Entry(0, "e", 5)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "d", 4), SortedFixture.Entry(1, "f", 6)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1);

        SortedPage first = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, null, 10, Token);
        SortedPage second = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, first.Next, 10, Token);
        SortedPage third = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, second.Next, 10, Token);

        first.Ids().ShouldBe(["s0/a", "s1/b"]);
        second.Ids().ShouldBe(["s0/c", "s1/d"]);
        third.Ids().ShouldBe(["s0/e", "s1/f"]);
        second.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 search count=2 c@3", "s1 search count=2 d@4"]);
        third.Next.ShouldBeNull();
        client.Calls.Skip(2).Select(c => c.Search).ShouldBe([.. first.Next!.Streams.Select(s => s.Search), .. second.Next.Streams.Select(s => s.Search)]);
        (second.Fetches, third.Fetches).ShouldBe((2, 2));
    }

    [Fact]
    public async Task Redeem_MixedEndAndStartSnapshot_CallsOnlyLiveOwners_WithTheirSavedCounts()
    {
        var client = new FakeShardClient();
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        client.Chain(2, [SortedFixture.Entry(2, "d", 4), SortedFixture.Entry(2, "e", 5)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3, 1));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 2, 0, 1);
        var resumeFrom = new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 5,
        [
            SortedStreamContinuation.AtEnd(Fixture.Shard(0), SortedFixture.Anchor(0, "a", 1)),
            SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, pageSize: 2), null),
            SortedStreamContinuation.AtSearch(SortedFixture.Stream(2, pageSize: 13), null),
        ]);

        SortedPage page = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 5, resumeFrom, 10, Token);

        page.Ids().ShouldBe(["s1/b", "s1/c", "s2/d", "s2/e"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s1 count=2", "search s2 count=13"]);
        client.Calls.Select(c => c.Search).ShouldBe([resumeFrom.Streams[1].Search, resumeFrom.Streams[2].Search]);
        page.Fetches.ShouldBe(2);
        page.Next.ShouldBeNull();
    }

    [Fact]
    public async Task Redeem_ReplaysTheSavedCursorVerbatim_AndCarriesItsCycleStateAndPrecedingInstant()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "b", 2), SortedFixture.Entry(0, "c", 3)], [SortedFixture.Entry(0, "d", 4)], [SortedFixture.Entry(0, "e", 5)]);
        client.Chain(1, [SortedFixture.Entry(1, "z", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1);

        SortedPage first = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, null, 10, Token);
        SortedPage second = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, first.Next, 10, Token);
        SortedPage third = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, second.Next, 10, Token);

        first.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 b@2 power=2 steps=0 preceding=1", "s1 search count=2 -"]);
        second.Ids().ShouldBe(["s0/c", "s0/d"]);
        second.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p2 d@4 power=2 steps=1 preceding=3", "s1 search count=2 -"]);
        third.Ids().ShouldBe(["s0/e", "s1/z"]);
        third.Next.ShouldBeNull();
        client.Calls.Skip(3).Select(c => c.ToString()).ShouldBe(["continue s0", "search s1 count=2", "continue s0", "continue s0", "search s1 count=2", "continue s0"]);
        client.Calls[3].Cursor.ShouldBeSameAs(first.Next.Streams[0].Cursor);
        client.Calls[6].Cursor.ShouldBeSameAs(second.Next.Streams[0].Cursor);
    }

    [Fact]
    public async Task Redeem_ReplayedCursorPageLinkingBackToThatCursor_FailsBeforeAnyFurtherCall()
    {
        var client = new FakeShardClient();
        ShardCursor[] cursors = client.Chain(
            0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "b", 2)], [SortedFixture.Entry(0, "c", 3)], [SortedFixture.Entry(0, "d", 4)]);
        client.Chain(1, [SortedFixture.Entry(1, "z", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1);
        SortedPage first = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, null, 10, Token);
        first.Next.ShouldNotBeNull().Describe()[0].ShouldBe("s0 cursor p2 c@3 power=2 steps=1 preceding=2");
        client.OnContinue(cursors[1], Fixture.Page(cursors[1], [SortedFixture.Entry(0, "c", 3), SortedFixture.Entry(0, "d", 4)]));
        int before = client.Calls.Count;

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, first.Next, 10, Token));

        (failure.Shard, failure.Reason).ShouldBe((Fixture.Shard(0), ShardFailureReason.ContractViolation));
        client.Calls.Skip(before).Select(c => c.ToString()).ShouldBe(["continue s0"]);
    }

    [Fact]
    public async Task FetchedButUnemittedSuccessor_LeavesTheCursorCheckpointAtItsInputCycleState()
    {
        var client = new FakeShardClient();
        client.Chain(0, [], [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "z", 9)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2", "continue s0", "continue s0"]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 a@1 power=2 steps=0 preceding=-", "s1 search count=2 b@2"]);
    }

    [Fact]
    public async Task FetchedButUnemittedSuccessor_AfterANonemptyPage_KeepsThePrecedingInstantOfTheCheckpoint()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "b", 2)], [SortedFixture.Entry(0, "z", 9)]);
        client.Chain(1, [SortedFixture.Entry(1, "c", 3), SortedFixture.Entry(1, "d", 4)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 3, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s0/b", "s1/c"]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 b@2 power=2 steps=0 preceding=1", "s1 search count=3 c@3"]);
    }

    [Fact]
    public async Task OwnerExhaustedWithoutEmitting_IsAnEndMarkerWithoutAnchor_BesideALiveOwner()
    {
        var client = new FakeShardClient();
        client.Chain(0, [[]]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 1, null, 10, Token);

        page.Ids().ShouldBe(["s1/b"]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 end -", "s1 search count=1 b@2"]);
    }

    [Fact]
    public async Task ExhaustionFoundOnATrailingEmptyTerminalPage_IsAnEndMarkerWithTheLastEmission()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], []);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2", "continue s0"]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 end a@1", "s1 search count=2 b@2"]);
    }
}

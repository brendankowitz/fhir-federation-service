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

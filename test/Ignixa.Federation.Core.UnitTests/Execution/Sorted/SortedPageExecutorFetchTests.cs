using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>What each stream is asked for, and when (design Q4, Q7 and algorithm steps 5 to 7).</summary>
public sealed class SortedPageExecutorFetchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShortNonterminalPage_IsFollowedWhenDrained_ButNotOnceThePageIsFull()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "b", 3)], [SortedFixture.Entry(0, "z", 99)]);
        client.Chain(1, [SortedFixture.Entry(1, "x", 2), SortedFixture.Entry(1, "y", 4)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 3, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/x", "s0/b"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=3", "search s1 count=3", "continue s0"]);
        client.Calls[2].Cursor.ShouldBe(Fixture.Cursor(0, "p1"));
        page.Fetches.ShouldBe(3);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 b@3 power=2 steps=0 preceding=1", "s1 search count=3 x@2"]);
    }

    [Fact]
    public async Task EmptyPagesWithNext_AreNotExhaustion_AndHeadsArePrimedInOrdinalRounds()
    {
        var client = new FakeShardClient();
        client.Chain(0, [], [], [SortedFixture.Entry(0, "a", 5), SortedFixture.Entry(0, "b", 8)]);
        client.Chain(1, [SortedFixture.Entry(1, "x", 2)], [], [SortedFixture.Entry(1, "y", 6)], [SortedFixture.Entry(1, "z", 10)]);
        client.Chain(2, [], [SortedFixture.Entry(2, "k", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 2, 0, 1), LastUpdatedSort.Ascending, 3, null, 10, Token);

        page.Ids().ShouldBe(["s1/x", "s0/a", "s1/y"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(
        [
            "search s0 count=3", "search s1 count=3", "search s2 count=3",
            "continue s0", "continue s2",
            "continue s0",
            "continue s1", "continue s1",
        ]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(
        [
            "s0 cursor p2 a@5 power=2 steps=1 preceding=-",
            "s1 cursor p2 y@6 power=2 steps=1 preceding=2",
            "s2 cursor p1 - power=2 steps=0 preceding=-",
        ]);
    }

    [Fact]
    public async Task SuccessorReadAheadButNotEmittedFrom_DoesNotDisplaceTheAnchoredCheckpoint()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "z", 9)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2", "search s1 count=2", "continue s0"]);
        page.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 search count=2 a@1", "s1 search count=2 b@2"]);
    }

    [Fact]
    public async Task ExactFillOnEveryOwnersLastEntry_HasNoSuccessor()
    {
        var client = new FakeShardClient();
        client.Chain(0, [], [SortedFixture.Entry(0, "a", 1)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/b"]);
        page.Next.ShouldBeNull();
    }
}

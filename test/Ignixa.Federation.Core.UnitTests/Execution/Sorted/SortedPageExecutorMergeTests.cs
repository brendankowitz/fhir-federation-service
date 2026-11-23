using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>Matches from every stream are merged by instant, then registry ordinal, then the shard's own order (design Q6).</summary>
public sealed class SortedPageExecutorMergeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OverlappingStreams_Ascending_MergeIntoTheOracleOrder()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 4), SortedFixture.Entry(0, "c", 7)]);
        client.Chain(1, [SortedFixture.Entry(1, "d", 2), SortedFixture.Entry(1, "e", 4), SortedFixture.Entry(1, "f", 9)]);
        client.Chain(2, [SortedFixture.Entry(2, "g", 3), SortedFixture.Entry(2, "h", 5), SortedFixture.Entry(2, "i", 6)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 2, 0, 1), LastUpdatedSort.Ascending, 20, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/d", "s2/g", "s0/b", "s1/e", "s2/h", "s2/i", "s0/c", "s1/f"]);
        page.Next.ShouldBeNull();
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=20", "search s1 count=20", "search s2 count=20"]);
        page.Fetches.ShouldBe(3);
    }

    [Fact]
    public async Task OverlappingStreams_Descending_MergeIntoTheOracleOrder()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "c", 7), SortedFixture.Entry(0, "b", 4), SortedFixture.Entry(0, "a", 1)]);
        client.Chain(1, [SortedFixture.Entry(1, "f", 9), SortedFixture.Entry(1, "e", 4), SortedFixture.Entry(1, "d", 2)]);
        client.Chain(2, [SortedFixture.Entry(2, "i", 6), SortedFixture.Entry(2, "h", 5), SortedFixture.Entry(2, "g", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Descending, 1, 2, 0), LastUpdatedSort.Descending, 20, null, 10, Token);

        page.Ids().ShouldBe(["s1/f", "s0/c", "s2/i", "s2/h", "s0/b", "s1/e", "s2/g", "s1/d", "s0/a"]);
        page.Next.ShouldBeNull();
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending)]
    [InlineData(LastUpdatedSort.Descending)]
    public async Task EqualInstantsAcrossOwners_DrainInRegistryOrder_WhateverTheirOffsets(LastUpdatedSort direction)
    {
        DateTimeOffset tie = SortedFixture.At(5);
        ShardEntry x = SortedFixture.Entry(0, "x", tie.ToOffset(TimeSpan.FromHours(2)));
        ShardEntry w = SortedFixture.Entry(0, "w", 8);
        ShardEntry y = SortedFixture.Entry(1, "y", tie);
        ShardEntry z = SortedFixture.Entry(2, "z", tie.ToOffset(TimeSpan.FromHours(-5)));
        ShardEntry v = SortedFixture.Entry(2, "v", 1);
        bool ascending = direction == LastUpdatedSort.Ascending;
        var client = new FakeShardClient();
        client.Chain(0, ascending ? [x, w] : [w, x]);
        client.Chain(1, [y]);
        client.Chain(2, ascending ? [v, z] : [z, v]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        SortedPage page = await executor.ExecuteAsync(SortedFixture.Streams(direction, 2, 1, 0), direction, 20, null, 10, Token);

        page.Ids().ShouldBe(ascending
            ? ["s2/v", "s0/x", "s1/y", "s2/z", "s0/w"]
            : ["s0/w", "s0/x", "s1/y", "s2/z", "s2/v"]);
    }

    [Fact]
    public async Task SameTypeAndIdOnTwoOwners_EmitsBothOriginalEntries()
    {
        ShardEntry first = SortedFixture.Entry(0, "p", 3);
        ShardEntry second = SortedFixture.Entry(1, "p", 2);
        var client = new FakeShardClient();
        client.Chain(0, [first]);
        client.Chain(1, [second]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 20, null, 10, Token);

        page.Matches.Count.ShouldBe(2);
        page.Matches[0].ShouldBeSameAs(second);
        page.Matches[1].ShouldBeSameAs(first);
        page.Matches.Select(m => m.Key).ShouldBe([Fixture.Key(1, "p"), Fixture.Key(0, "p")]);
        page.Matches.Select(m => m.VersionId).ShouldBe(["v-s1-p", "v-s0-p"]);
        page.Matches[0].Resource.ToArray().ShouldBe(second.Resource.ToArray());
        page.Matches[0].Resource.ToArray().ShouldNotBe(page.Matches[1].Resource.ToArray());
    }

    [Fact]
    public async Task Matches_CannotBeMutatedThroughADowncast()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1)]);
        client.Chain(1, [SortedFixture.Entry(1, "b", 2)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 20, null, 10, Token);

        IList<ShardEntry> downcast = page.Matches.ShouldBeAssignableTo<IList<ShardEntry>>();
        Should.Throw<NotSupportedException>(() => downcast.Add(SortedFixture.Entry(0, "z", 9)));
        Should.Throw<NotSupportedException>(() => downcast.Clear());
        Should.Throw<NotSupportedException>(() => downcast[0] = SortedFixture.Entry(0, "z", 9));
        page.Ids().ShouldBe(["s0/a", "s1/b"]);
    }

    [Fact]
    public async Task CutPage_SavesEveryOwnersCheckpointInRegistryOrder()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 4)]);
        client.Chain(1, [SortedFixture.Entry(1, "d", 2)]);
        client.Chain(2, [SortedFixture.Entry(2, "g", 3), SortedFixture.Entry(2, "h", 5)]);
        client.Chain(3, [SortedFixture.Entry(3, "k", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(4));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 3, 1, 2, 0), LastUpdatedSort.Ascending, 3, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/d", "s2/g"]);
        SortedContinuation next = page.Next.ShouldNotBeNull();
        next.Type.ShouldBe(Fixture.Patient);
        next.Direction.ShouldBe(LastUpdatedSort.Ascending);
        next.PageSize.ShouldBe(3);
        next.Describe().ShouldBe(["s0 search count=3 a@1", "s1 end d@2", "s2 search count=3 g@3", "s3 search count=3 -"]);
        next.Streams[0].Search.ShouldBeSameAs(client.Calls.Single(c => c.Shard == Fixture.Shard(0)).Search);
        next.Streams[3].Search.ShouldBeSameAs(client.Calls.Single(c => c.Shard == Fixture.Shard(3)).Search);
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending, 20)]
    [InlineData(LastUpdatedSort.Ascending, 4)]
    [InlineData(LastUpdatedSort.Descending, 20)]
    [InlineData(LastUpdatedSort.Descending, 4)]
    public async Task MultiPageStreamsWithDelayedFirstHead_MergeIntoTheOracleOrder(LastUpdatedSort direction, int pageSize)
    {
        var client = new FakeShardClient();
        string[] oracle = ScriptDelayedOverlap(client, direction);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        SortedPage page = await executor.ExecuteAsync(SortedFixture.Streams(direction, 1, 2, 0), direction, pageSize, null, 20, Token);

        page.Ids().ShouldBe(oracle.Take(pageSize));
        (page.Next is null).ShouldBe(pageSize >= oracle.Length);
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending, 1)]
    [InlineData(LastUpdatedSort.Ascending, 2)]
    [InlineData(LastUpdatedSort.Ascending, 4)]
    [InlineData(LastUpdatedSort.Descending, 1)]
    [InlineData(LastUpdatedSort.Descending, 3)]
    [InlineData(LastUpdatedSort.Descending, 4)]
    public async Task PagesConcatenatedAcrossContinuations_EqualTheOracleOrder(LastUpdatedSort direction, int pageSize)
    {
        var client = new FakeShardClient();
        string[] oracle = ScriptDelayedOverlap(client, direction);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));

        List<SortedPage> pages = await executor.ReadAllAsync(SortedFixture.Streams(direction, 1, 2, 0), direction, pageSize);

        pages.SelectMany(p => p.Ids()).ShouldBe(oracle);
        pages.SkipLast(1).ShouldAllBe(p => p.Matches.Count == pageSize);
    }

    /// <summary>
    /// Scripts three overlapping multi-page streams, s0 with a leading empty page and a tie with s1 at instant 4, in
    /// <paramref name="direction"/>.
    /// </summary>
    /// <returns>The hand-ordered emission order of every match.</returns>
    private static string[] ScriptDelayedOverlap(FakeShardClient client, LastUpdatedSort direction)
    {
        ShardEntry a = SortedFixture.Entry(0, "a", 1), b = SortedFixture.Entry(0, "b", 4), c = SortedFixture.Entry(0, "c", 7);
        ShardEntry d = SortedFixture.Entry(1, "d", 2), e = SortedFixture.Entry(1, "e", 4), f = SortedFixture.Entry(1, "f", 9);
        ShardEntry g = SortedFixture.Entry(2, "g", 3), h = SortedFixture.Entry(2, "h", 5), i = SortedFixture.Entry(2, "i", 6);
        if (direction == LastUpdatedSort.Ascending)
        {
            client.Chain(0, [], [a, b], [c]);
            client.Chain(1, [d], [e, f]);
            client.Chain(2, [g, h, i]);
            return ["s0/a", "s1/d", "s2/g", "s0/b", "s1/e", "s2/h", "s2/i", "s0/c", "s1/f"];
        }

        client.Chain(0, [], [c, b], [a]);
        client.Chain(1, [f, e], [d]);
        client.Chain(2, [i, h, g]);
        return ["s1/f", "s0/c", "s2/i", "s2/h", "s0/b", "s1/e", "s2/g", "s1/d", "s0/a"];
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending)]
    [InlineData(LastUpdatedSort.Descending)]
    public async Task EqualInstantsWithinOneOwner_KeepItsOwnOrder_AcrossNext(LastUpdatedSort direction)
    {
        bool ascending = direction == LastUpdatedSort.Ascending;
        ShardEntry j = SortedFixture.Entry(1, "j", 3), n = SortedFixture.Entry(1, "n", 5);
        var client = new FakeShardClient();
        client.Chain(
            0,
            [SortedFixture.Entry(0, "m", 5), SortedFixture.Entry(0, "c", 5)],
            [SortedFixture.Entry(0, "q", 5), SortedFixture.Entry(0, "b", 5)]);
        client.Chain(1, ascending ? [j, n] : [n, j]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        SortedPage page = await executor.ExecuteAsync(SortedFixture.Streams(direction, 1, 0), direction, 20, null, 10, Token);

        page.Ids().ShouldBe(ascending
            ? ["s1/j", "s0/m", "s0/c", "s0/q", "s0/b", "s1/n"]
            : ["s0/m", "s0/c", "s0/q", "s0/b", "s1/n", "s1/j"]);
    }
}

using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>
/// Cyclic <c>next</c> links are a shard failure even when the cycle only shows across continuations (design spec §7.8).
/// Every backend page here holds exactly the page size, so each federation page ends at a backend page boundary and makes
/// one shard call; the per-execution repeated-cursor check never sees two cursors of the cycle.
/// </summary>
public sealed class UnsortedPageExecutorCycleTests
{
    private const int PageSize = 2;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CycleAToBToAAcrossRedemptions_IsAContractViolationWithinABoundedNumberOfPages()
    {
        var client = new FakeShardClient();
        ScriptChain(client, prefix: 0, cycle: 2);

        ShardFailureException thrown = await ChainUntilFailureAsync(client, maxPages: 4);

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        thrown.InnerException.ShouldBeNull();
        thrown.Message.ShouldNotContain(Fixture.SecretValue);
        thrown.Message.ShouldNotContain("ct=");
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    [InlineData(2, 5)]
    [InlineData(5, 7)]
    [InlineData(9, 4)]
    public async Task LongerCycleAfterAPrefixAcrossRedemptions_IsAContractViolationWithinABoundedNumberOfPages(int prefix, int cycle)
    {
        var client = new FakeShardClient();
        ScriptChain(client, prefix, cycle);

        ShardFailureException thrown = await ChainUntilFailureAsync(client, maxPages: (3 * (prefix + cycle)) + 2);

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Fact]
    public async Task RedeemingTheSameContinuationsAgain_GivesIdenticalPagesAndNextsWithNoFalseCycle()
    {
        var client = new FakeShardClient();
        ShardCursor first = Fixture.Cursor(0, "1");
        ShardCursor second = Fixture.Cursor(0, "2");
        client.OnSearch(0, Fixture.Page(first, Fixture.Entries(0, "a", "b")));
        client.OnContinue(first, Fixture.Page(second, Fixture.Entries(0, "c", "d", "e", "f")));
        client.OnContinue(second, Fixture.Page(null, Fixture.Entries(0, "g", "h")));
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(1));
        ShardSearchRequest[] streams = Fixture.Streams(1);

        UnsortedContinuation atFirst = (await executor.ExecuteAsync(streams, PageSize, null, 10, Token)).Next.ShouldNotBeNull();
        UnsortedPage partial = await executor.ExecuteAsync(streams, PageSize, atFirst, 10, Token);
        UnsortedPage partialAgain = await executor.ExecuteAsync(streams, PageSize, atFirst, 10, Token);
        UnsortedContinuation inside = partial.Next.ShouldNotBeNull();
        UnsortedPage replay = await executor.ExecuteAsync(streams, PageSize, inside, 10, Token);
        UnsortedPage replayAgain = await executor.ExecuteAsync(streams, PageSize, inside, 10, Token);
        UnsortedPage replayThird = await executor.ExecuteAsync(streams, PageSize, inside, 10, Token);
        UnsortedPage last = await executor.ExecuteAsync(streams, PageSize, replay.Next.ShouldNotBeNull(), 10, Token);

        partial.Ids().ShouldBe(Fixture.Ids(0, "c", "d"));
        ShouldBeIdentical(partialAgain, partial);
        inside.Cursor.ShouldBe(first);
        inside.LastEmitted.ShouldBe(Fixture.Key(0, "d"));
        inside.CycleState.ShouldBe(atFirst.CycleState);
        replay.Ids().ShouldBe(Fixture.Ids(0, "e", "f"));
        ShouldBeIdentical(replayAgain, replay);
        ShouldBeIdentical(replayThird, replay);
        UnsortedContinuation atSecond = replay.Next.ShouldNotBeNull();
        atSecond.Cursor.ShouldBe(second);
        atSecond.LastEmitted.ShouldBeNull();
        last.Ids().ShouldBe(Fixture.Ids(0, "g", "h"));
        last.Next.ShouldBeNull();
    }

    [Fact]
    public async Task LongChainOfDistinctCursorsAcrossRedemptions_NeverTriggersAndReproducesTheFullResult()
    {
        const int Links = 40;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(Fixture.Cursor(0, "0"), Fixture.Entries(0, "start-a", "start-b")));
        for (int i = 0; i < Links; i++)
        {
            ShardCursor? next = i + 1 < Links ? Fixture.Cursor(0, $"{i + 1}") : null;
            client.OnContinue(Fixture.Cursor(0, $"{i}"), Fixture.Page(next, Fixture.Entries(0, $"p{i}-a", $"p{i}-b")));
        }

        client.Serve(1, ["x", "y", "z"]);
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(2));
        var all = new List<string>();
        UnsortedContinuation? resume = null;
        int pages = 0;
        do
        {
            UnsortedPage page = await executor.ExecuteAsync(Fixture.Streams(2), PageSize, resume, 10, Token);
            all.AddRange(page.Ids());
            resume = page.Next;
            pages++;
        }
        while (resume is not null && pages < 100);

        string[] expected =
        [
            .. Fixture.Ids(0, "start-a", "start-b"),
            .. Enumerable.Range(0, Links).SelectMany(i => Fixture.Ids(0, $"p{i}-a", $"p{i}-b")),
            .. Fixture.Ids(1, "x", "y", "z"),
        ];
        all.ShouldBe(expected);
        resume.ShouldBeNull();
    }

    /// <summary>
    /// Scripts shard 0 as a first page followed by <paramref name="prefix"/> + <paramref name="cycle"/> distinct cursors,
    /// where the last cursor links back to cursor <paramref name="prefix"/>. Every page holds <see cref="PageSize"/>
    /// entries with ids unique to that page.
    /// </summary>
    private static void ScriptChain(FakeShardClient client, int prefix, int cycle)
    {
        int count = prefix + cycle;
        client.OnSearch(0, Fixture.Page(Fixture.Cursor(0, "0"), Fixture.Entries(0, "start-a", "start-b")));
        for (int i = 0; i < count; i++)
        {
            int successor = i + 1 < count ? i + 1 : prefix;
            client.OnContinue(Fixture.Cursor(0, $"{i}"), Fixture.Page(Fixture.Cursor(0, $"{successor}"), Fixture.Entries(0, $"p{i}-a", $"p{i}-b")));
        }
    }

    private static async Task<ShardFailureException> ChainUntilFailureAsync(FakeShardClient client, int maxPages)
    {
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(2));
        UnsortedContinuation? resume = null;
        for (int pages = 1; pages <= maxPages; pages++)
        {
            UnsortedPage page;
            try
            {
                page = await executor.ExecuteAsync(Fixture.Streams(2), PageSize, resume, 10, Token);
            }
            catch (ShardFailureException failure)
            {
                return failure;
            }

            // One call per page: the cycle is invisible to the per-execution repeated-cursor check.
            page.Fetches.ShouldBe(1);
            page.Matches.Count.ShouldBe(PageSize);
            resume = page.Next.ShouldNotBeNull();
            resume.Shard.ShouldBe(Fixture.Shard(0));
        }

        throw new ShouldAssertException($"The cycle was not detected within {maxPages} pages.");
    }

    private static void ShouldBeIdentical(UnsortedPage actual, UnsortedPage expected)
    {
        actual.Ids().ShouldBe(expected.Ids());
        actual.Fetches.ShouldBe(expected.Fetches);
        UnsortedContinuation actualNext = actual.Next.ShouldNotBeNull();
        UnsortedContinuation expectedNext = expected.Next.ShouldNotBeNull();
        actualNext.Shard.ShouldBe(expectedNext.Shard);
        actualNext.Search.ShouldBe(expectedNext.Search);
        actualNext.Cursor.ShouldBe(expectedNext.Cursor);
        actualNext.LastEmitted.ShouldBe(expectedNext.LastEmitted);
        actualNext.CycleState.ShouldBe(expectedNext.CycleState);
    }
}

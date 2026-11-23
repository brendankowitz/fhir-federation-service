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

    /// <summary>An owner redeemed at its end stays at its end, and carries its saved anchor into every later snapshot.</summary>
    [Fact]
    public async Task Redeem_AnchoredEndBesideALiveOwner_KeepsTheEndAndItsAnchorInTheNextSnapshot()
    {
        var client = new FakeShardClient();
        client.Chain(1, [SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)], [SortedFixture.Entry(1, "d", 4)]);

        SortedPage page = await client.Resume(
            LastUpdatedSort.Ascending,
            2,
            SortedStreamContinuation.AtEnd(Fixture.Shard(0), SortedFixture.Anchor(0, "a", 1)),
            SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, pageSize: 2), null));

        page.Ids().ShouldBe(["s1/b", "s1/c"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s1 count=2"]);
        SortedContinuation next = page.Next.ShouldNotBeNull();
        next.Describe()[0].ShouldBe("s0 end a@1");
        next.Streams[0].LastEmitted.ShouldNotBeNull().Key.ShouldBe(Fixture.Key(0, "a"));
    }

    /// <summary>
    /// A posted owner on a profile that redeems <c>next</c> by reposting follows its cursor in the merge, saves it when the
    /// page is cut inside the cursor's page, and replays that same cursor on redemption.
    /// </summary>
    [Fact]
    public async Task PostedOwner_FollowsAndReplaysARepostToSearchCursor()
    {
        var client = new FakeShardClient();
        ShardCursor p1 = Fixture.Cursor(0, "p1", NextLinkRedemption.RepostToSearch);
        client.OnSearch(0, Fixture.Page(p1, SortedFixture.Entry(0, "a", 1)));
        client.OnContinue(p1, Fixture.Page(null, SortedFixture.Entry(0, "c", 3), SortedFixture.Entry(0, "e", 5)));
        client.OnSearch(1, Fixture.Page(null, SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "d", 4), SortedFixture.Entry(1, "f", 6)));
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2, 0));
        ShardSearchRequest[] streams = [SortedFixture.Stream(0, method: ShardSearchMethod.PostSearch), SortedFixture.Stream(1)];

        SortedPage first = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, null, 10, Token);
        SortedPage second = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, first.Next, 10, Token);

        first.Ids().ShouldBe(["s0/a", "s1/b", "s0/c"]);
        second.Ids().ShouldBe(["s1/d", "s0/e", "s1/f"]);
        first.Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 c@3 power=2 steps=0 preceding=1", "s1 search count=3 b@2"]);
        first.Next.Streams[0].Cursor.ShouldBe(p1);
        second.Next.ShouldBeNull();
        client.Calls.Select(c => c.ToString()).ShouldBe(
            ["search s0 count=2", "search s1 count=3", "continue s0", "continue s0", "search s1 count=3"]);
        client.Calls[0].Search.ShouldNotBeNull().Method.ShouldBe(ShardSearchMethod.PostSearch);
        client.Calls.Where(c => c.Cursor is not null).Select(c => c.Cursor).ShouldBe([p1, p1]);
        (first.Fetches, second.Fetches).ShouldBe((3, 2));
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

    public static TheoryData<string> ReplayInputs => ["search", "cursor"];

    [Theory]
    [MemberData(nameof(ReplayInputs))]
    public async Task Redeem_AnchorAbsentFromItsReplayedInput_IsMissing_EvenWhenASuccessorHoldsIt(string input)
    {
        var client = new FakeShardClient();
        ShardCursor p1 = Fixture.Cursor(0, "p1"), p2 = Fixture.Cursor(0, "p2");
        SortedStreamContinuation start = input == "search"
            ? SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 2), SortedFixture.Anchor(0, "a", 1))
            : SortedStreamContinuation.AtCursor(p1, SortedFixture.Anchor(0, "a", 1), new CursorCycleState(1UL, 2, 0), null);
        client.OnSearch(0, Fixture.Page(p2, []));
        client.OnContinue(p1, Fixture.Page(p2, []));
        client.OnContinue(p2, Fixture.Page(null, SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "c", 3)));

        ContinuationAnchorMissingException missing = await Should.ThrowAsync<ContinuationAnchorMissingException>(
            () => client.Resume(LastUpdatedSort.Ascending, 2, start, SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, pageSize: 2), null)));

        missing.Shard.ShouldBe(Fixture.Shard(0));
        client.Calls.Select(c => c.Shard).ShouldBe([Fixture.Shard(0)]);
    }

    public static TheoryData<string, ShardEntry[]> MovedAnchors => new()
    {
        { "later instant", [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 3), SortedFixture.Entry(0, "c", 4)] },
        { "later instant, shifted past a neighbour", [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "c", 3), SortedFixture.Entry(0, "b", 5)] },
        { "earlier instant, out of order", [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 0), SortedFixture.Entry(0, "c", 3)] },
    };

    [Theory]
    [MemberData(nameof(MovedAnchors))]
    public async Task Redeem_AnchorKeyAtAnotherInstant_IsMissing(string because, ShardEntry[] replayed)
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, replayed));

        ContinuationAnchorMissingException missing = await Should.ThrowAsync<ContinuationAnchorMissingException>(
            () => client.Resume(LastUpdatedSort.Ascending, 2, SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 3), SortedFixture.Anchor(0, "b", 2))),
            because);

        missing.Shard.ShouldBe(Fixture.Shard(0));
    }

    [Fact]
    public async Task Redeem_AnchorKeyAtAnotherInstant_AsTheLastEntryBeforeASuccessor_IsMissing_WithoutFollowing()
    {
        var client = new FakeShardClient();
        ShardCursor p1 = Fixture.Cursor(0, "p1");
        client.OnSearch(0, Fixture.Page(p1, SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 5)));
        client.OnContinue(p1, Fixture.Page(null, SortedFixture.Entry(0, "b", 6)));

        await Should.ThrowAsync<ContinuationAnchorMissingException>(
            () => client.Resume(LastUpdatedSort.Ascending, 2, SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 2), SortedFixture.Anchor(0, "b", 2))));

        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=2"]);
    }

    [Fact]
    public async Task Redeem_AnchorInstantSpelledWithAnotherOffset_IsTheSameAnchor()
    {
        var client = new FakeShardClient();
        DateTimeOffset sameInstant = SortedFixture.At(2).ToOffset(TimeSpan.FromHours(5));
        client.OnSearch(0, Fixture.Page(null, SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", sameInstant), SortedFixture.Entry(0, "c", 3)));

        SortedPage page = await client.Resume(LastUpdatedSort.Ascending, 2, SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 3), SortedFixture.Anchor(0, "b", 2)));

        page.Ids().ShouldBe(["s0/c"]);
        page.Next.ShouldBeNull();
    }

    [Fact]
    public async Task Redeem_UnanchoredCursorAfterLeadingEmpties_ReplaysFromEntryZero_AndAnEndOwnerIsNotReplayed()
    {
        var client = new FakeShardClient();
        client.Chain(0, [], [SortedFixture.Entry(0, "x", 5), SortedFixture.Entry(0, "y", 6)]);
        client.Chain(1, [SortedFixture.Entry(1, "a", 1), SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "c", 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        List<SortedPage> pages = await executor.ReadAllAsync(SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2);

        pages.Select(p => p.Ids()).ShouldBe([["s1/a", "s1/b"], ["s1/c", "s0/x"], ["s0/y"]]);
        pages[0].Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 - power=2 steps=0 preceding=-", "s1 search count=2 b@2"]);
        pages[1].Next.ShouldNotBeNull().Describe().ShouldBe(["s0 cursor p1 x@5 power=2 steps=0 preceding=-", "s1 end c@3"]);
        client.Calls.Skip(3).Select(c => c.ToString()).ShouldBe(["continue s0", "search s1 count=2", "continue s0"]);
    }

    [Fact]
    public async Task Redeem_ExactFillOnAPagesLastEntry_ReplaysTheAnchorHoldingInput_ThenFollowsItsSuccessor()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 2)], [SortedFixture.Entry(0, "c", 3)]);
        client.Chain(1, [SortedFixture.Entry(1, "z", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        List<SortedPage> pages = await executor.ReadAllAsync(SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2);

        pages.Select(p => p.Ids()).ShouldBe([["s0/a", "s0/b"], ["s0/c", "s1/z"]]);
        pages[0].Next.ShouldNotBeNull().Describe().ShouldBe(["s0 search count=2 b@2", "s1 search count=2 -"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(
            ["search s0 count=2", "search s1 count=2", "search s0 count=2", "search s1 count=2", "continue s0"]);
    }

    [Fact]
    public async Task Redeem_WhenEveryOwnerEndsOnItsReplay_IsAnEmptyLastPage()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "b", 2)], []);
        client.Chain(1, [SortedFixture.Entry(1, "a", 1)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        List<SortedPage> pages = await executor.ReadAllAsync(SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 2);

        pages.Select(p => p.Ids()).ShouldBe([["s1/a", "s0/b"], []]);
        pages[0].Next.ShouldNotBeNull().Describe().ShouldBe(["s0 search count=2 b@2", "s1 end a@1"]);
        client.Calls.Skip(2).Select(c => c.ToString()).ShouldBe(["search s0 count=2", "continue s0"]);
        pages[1].Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task Redeem_TheSameSnapshotTwice_ReproducesTheMatchesTheCallsAndTheSuccessor()
    {
        var client = new FakeShardClient();
        client.Chain(0, [], [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 4)], [SortedFixture.Entry(0, "c", 7)]);
        client.Chain(1, [SortedFixture.Entry(1, "d", 2)], [SortedFixture.Entry(1, "e", 4), SortedFixture.Entry(1, "f", 9)]);
        client.Chain(2, [SortedFixture.Entry(2, "g", 3), SortedFixture.Entry(2, "h", 5), SortedFixture.Entry(2, "i", 6)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(3));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 2, 0, 1);
        SortedContinuation snapshot = (await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, null, 10, Token)).Next!;

        int before = client.Calls.Count;
        SortedPage once = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, snapshot, 10, Token);
        int between = client.Calls.Count;
        SortedPage twice = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 3, snapshot, 10, Token);

        once.Ids().ShouldBe(["s0/b", "s1/e", "s2/h"]);
        twice.Ids().ShouldBe(once.Ids());
        twice.Matches.Select(m => Convert.ToBase64String(m.Resource.Span)).ShouldBe(once.Matches.Select(m => Convert.ToBase64String(m.Resource.Span)));
        FakeCall[] onceCalls = [.. client.Calls.Skip(before).Take(between - before)], twiceCalls = [.. client.Calls.Skip(between)];
        twiceCalls.Select(c => c.ToString()).ShouldBe(onceCalls.Select(c => c.ToString()));
        for (int i = 0; i < onceCalls.Length; i++)
        {
            twiceCalls[i].Search.ShouldBeSameAs(onceCalls[i].Search);
            twiceCalls[i].Cursor.ShouldBeSameAs(onceCalls[i].Cursor);
        }

        twice.Fetches.ShouldBe(once.Fetches);
        SortedContinuation onceNext = once.Next.ShouldNotBeNull(), twiceNext = twice.Next.ShouldNotBeNull();
        twiceNext.Describe().ShouldBe(onceNext.Describe());
        for (int i = 0; i < onceNext.Streams.Count; i++)
        {
            twiceNext.Streams[i].Search.ShouldBeSameAs(onceNext.Streams[i].Search);
            twiceNext.Streams[i].Cursor.ShouldBeSameAs(onceNext.Streams[i].Cursor);
            twiceNext.Streams[i].CycleState.ShouldBe(onceNext.Streams[i].CycleState);
            (twiceNext.Streams[i].LastEmitted?.Key, twiceNext.Streams[i].LastEmitted?.LastUpdated)
                .ShouldBe((onceNext.Streams[i].LastEmitted?.Key, onceNext.Streams[i].LastEmitted?.LastUpdated));
        }
    }

    [Fact]
    public async Task Redeem_ACursorReplayedOnEveryPageAcrossTies_RaisesNoFalseCycle()
    {
        var client = new FakeShardClient();
        client.Chain(
            0, [], [SortedFixture.Entry(0, "a", 5), SortedFixture.Entry(0, "b", 5), SortedFixture.Entry(0, "c", 5), SortedFixture.Entry(0, "d", 5)]);
        client.Chain(1, [SortedFixture.Entry(1, "z", 9)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1);

        List<SortedPage> pages = await executor.ReadAllAsync(streams, LastUpdatedSort.Ascending, 1);
        SortedPage again = await executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 1, pages[1].Next, 10, Token);

        pages.SelectMany(p => p.Ids()).ShouldBe(["s0/a", "s0/b", "s0/c", "s0/d", "s1/z"]);
        pages.Take(3).Select(p => p.Next!.Describe()[0]).ShouldBe(
        [
            "s0 cursor p1 a@5 power=2 steps=0 preceding=-",
            "s0 cursor p1 b@5 power=2 steps=0 preceding=-",
            "s0 cursor p1 c@5 power=2 steps=0 preceding=-",
        ]);
        again.Ids().ShouldBe(pages[2].Ids());
        again.Next.ShouldNotBeNull().Describe().ShouldBe(pages[2].Next!.Describe());
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(1, 5)]
    public async Task CursorCycleSpanningFederationPages_IsAContractViolationWithinABoundedNumberOfPages(int prefix, int cycle)
    {
        var client = new FakeShardClient();
        int links = prefix + cycle;
        client.OnSearch(0, Fixture.Page(Fixture.Cursor(0, "p0"), SortedFixture.Entry(0, "start", 5)));
        for (int i = 0; i < links; i++)
        {
            int successor = i + 1 < links ? i + 1 : prefix;
            client.OnContinue(Fixture.Cursor(0, $"p{i}"), Fixture.Page(Fixture.Cursor(0, $"p{successor}"), SortedFixture.Entry(0, $"e{i}", 5)));
        }

        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(1));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => executor.ReadAllAsync(SortedFixture.Streams(LastUpdatedSort.Ascending, 0), LastUpdatedSort.Ascending, 1, maxPages: (3 * links) + 2));

        (failure.Shard, failure.Reason, failure.InnerException).ShouldBe((Fixture.Shard(0), ShardFailureReason.ContractViolation, null));
    }

    [Fact]
    public async Task LongAcyclicCursorChainAcrossFederationPages_Terminates()
    {
        const int Links = 100;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(Fixture.Cursor(0, "p0"), SortedFixture.Entry(0, "start", 5)));
        for (int i = 0; i < Links; i++)
        {
            ShardCursor? next = i + 1 < Links ? Fixture.Cursor(0, $"p{i + 1}") : null;
            client.OnContinue(Fixture.Cursor(0, $"p{i}"), Fixture.Page(next, SortedFixture.Entry(0, $"e{i}", 5)));
        }

        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(1));

        List<SortedPage> pages = await executor.ReadAllAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0), LastUpdatedSort.Ascending, 1, maxPages: Links + 2);

        pages.SelectMany(p => p.Ids()).ShouldBe(["s0/start", .. Enumerable.Range(0, Links).Select(i => $"s0/e{i}")]);
    }

    [Fact]
    public async Task Redeem_ASnapshotRebuiltFromItsPublicFields_OnAFreshExecutor_ContinuesIdentically()
    {
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 1, 2, 0);
        List<SortedPage> pages = await SortedFixture.Executor(Scripted(), Fixture.Registry(3)).ReadAllAsync(streams, LastUpdatedSort.Ascending, 2);

        for (int i = 0; i + 1 < pages.Count; i++)
        {
            SortedPage rebuilt = await SortedFixture.Executor(Scripted(), Fixture.Registry(3))
                .ExecuteAsync(streams, LastUpdatedSort.Ascending, 2, Rebuild(pages[i].Next!), 10, Token);

            rebuilt.Ids().ShouldBe(pages[i + 1].Ids());
            (rebuilt.Next?.Describe()).ShouldBe(pages[i + 1].Next?.Describe());
        }

        string[] shapes = [.. pages.SkipLast(1).SelectMany(p => p.Next!.Describe()).Select(d => d.Split(' ')[1]).Distinct()];
        shapes.ShouldBe(["cursor", "search", "end"], ignoreOrder: true);

        static FakeShardClient Scripted()
        {
            var client = new FakeShardClient();
            client.Chain(0, [], [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 4)], [SortedFixture.Entry(0, "c", 7)]);
            client.Chain(1, [SortedFixture.Entry(1, "d", 2)], [SortedFixture.Entry(1, "e", 4), SortedFixture.Entry(1, "f", 9)]);
            client.Chain(2, [SortedFixture.Entry(2, "g", 3), SortedFixture.Entry(2, "h", 5), SortedFixture.Entry(2, "i", 6)]);
            return client;
        }
    }

    /// <summary>Rebuilds a snapshot as a token codec would: new instances of every listed field, nothing else.</summary>
    private static SortedContinuation Rebuild(SortedContinuation saved) =>
        new(saved.Type, saved.Direction, saved.PageSize, [.. saved.Streams.Select(Rebuild)]);

    private static SortedStreamContinuation Rebuild(SortedStreamContinuation saved)
    {
        SortedAnchor? anchor = saved.LastEmitted is { } emitted ? new SortedAnchor(emitted.LastUpdated, emitted.Key) : null;
        if (saved.IsExhausted)
        {
            return SortedStreamContinuation.AtEnd(saved.Shard, anchor);
        }

        if (saved.Search is { } search)
        {
            return SortedStreamContinuation.AtSearch(
                new ShardSearchRequest(search.Shard, search.Type, [.. search.Parameters], search.PageSize, search.Method), anchor);
        }

        ShardCursor cursor = saved.Cursor!;
        CursorCycleState cycle = saved.CycleState;
        return SortedStreamContinuation.AtCursor(
            new ShardCursor(cursor.Shard, cursor.Type, cursor.Target, cursor.Redemption),
            anchor,
            new CursorCycleState(cycle.SavedFingerprint, cycle.Power, cycle.Steps),
            saved.PreviousPageLastUpdated);
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

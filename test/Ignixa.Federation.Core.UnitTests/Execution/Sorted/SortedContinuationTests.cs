using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>
/// The intrinsic shape of a sorted continuation, enforced where it is constructed (design Q8 and test 35); facts that need
/// the caller's plan are checked by the executor instead.
/// </summary>
public sealed class SortedContinuationTests
{
    private static readonly CursorCycleState AfterOneCursor = new(1UL, 2, 0);

    [Fact]
    public void Anchor_RequiresAKey()
    {
        Should.Throw<ArgumentNullException>(() => new SortedAnchor(SortedFixture.At(1), null!)).ParamName.ShouldBe("key");
    }

    [Fact]
    public void AtSearch_RequiresTheSentRequest()
    {
        Should.Throw<ArgumentNullException>(() => SortedStreamContinuation.AtSearch(null!, null)).ParamName.ShouldBe("sent");
    }

    [Fact]
    public void AtSearch_RejectsASentRequestWithoutAPositiveCount()
    {
        Should.Throw<ArgumentException>(() => SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 0), null))
            .ParamName.ShouldBe("sent");
    }

    [Theory]
    [InlineData(1, "Patient")]
    [InlineData(0, "Observation")]
    public void AtSearch_RejectsAnAnchorOfAnotherShardOrType(int ordinal, string type)
    {
        SortedAnchor anchor = SortedFixture.Anchor(ordinal, "a", 1, ResourceType.Parse(type));

        Should.Throw<ArgumentException>(() => SortedStreamContinuation.AtSearch(SortedFixture.Stream(0), anchor))
            .ParamName.ShouldBe("lastEmitted");
    }

    [Fact]
    public void AtCursor_RequiresTheCursorAndCycleState()
    {
        Should.Throw<ArgumentNullException>(() => SortedStreamContinuation.AtCursor(null!, null, AfterOneCursor, null))
            .ParamName.ShouldBe("cursor");
        Should.Throw<ArgumentNullException>(() => SortedStreamContinuation.AtCursor(Fixture.Cursor(0, "p1"), null, null!, null))
            .ParamName.ShouldBe("cycleState");
    }

    [Theory]
    [InlineData(1, "Patient")]
    [InlineData(0, "Observation")]
    public void AtCursor_RejectsAnAnchorOfAnotherShardOrType(int ordinal, string type)
    {
        SortedAnchor anchor = SortedFixture.Anchor(ordinal, "a", 1, ResourceType.Parse(type));

        Should.Throw<ArgumentException>(() => SortedStreamContinuation.AtCursor(Fixture.Cursor(0, "p1"), anchor, AfterOneCursor, null))
            .ParamName.ShouldBe("lastEmitted");
    }

    [Fact]
    public void AtCursor_WithoutAnchor_HasNoPrecedingInstant_BecauseOnlyLeadingEmptiesPrecedeIt()
    {
        Should.Throw<ArgumentException>(
                () => SortedStreamContinuation.AtCursor(Fixture.Cursor(0, "p1"), null, AfterOneCursor, SortedFixture.At(1)))
            .ParamName.ShouldBe("previousPageLastUpdated");
    }

    [Fact]
    public void AtEnd_RequiresTheShardAndRejectsAnAnchorOfAnotherShard()
    {
        Should.Throw<ArgumentNullException>(() => SortedStreamContinuation.AtEnd(null!, null)).ParamName.ShouldBe("shard");
        Should.Throw<ArgumentException>(() => SortedStreamContinuation.AtEnd(Fixture.Shard(0), SortedFixture.Anchor(1, "a", 1)))
            .ParamName.ShouldBe("lastEmitted");
    }

    [Fact]
    public void Factories_KeepTheirLegalShapes()
    {
        ShardSearchRequest sent = SortedFixture.Stream(0, pageSize: 7);
        ShardCursor cursor = Fixture.Cursor(0, "p1");
        SortedAnchor anchor = SortedFixture.Anchor(0, "a", 2);

        SortedStreamContinuation search = SortedStreamContinuation.AtSearch(sent, anchor);
        SortedStreamContinuation atCursor = SortedStreamContinuation.AtCursor(cursor, anchor, AfterOneCursor, SortedFixture.At(1));
        SortedStreamContinuation end = SortedStreamContinuation.AtEnd(Fixture.Shard(0), null);

        (search.Shard, search.Search, search.Cursor, search.IsExhausted, search.LastEmitted, search.CycleState, search.PreviousPageLastUpdated)
            .ShouldBe((Fixture.Shard(0), sent, (ShardCursor?)null, false, anchor, CursorCycleState.Initial, (DateTimeOffset?)null));
        (atCursor.Shard, atCursor.Search, atCursor.Cursor, atCursor.IsExhausted, atCursor.LastEmitted, atCursor.CycleState, atCursor.PreviousPageLastUpdated)
            .ShouldBe((Fixture.Shard(0), (ShardSearchRequest?)null, cursor, false, anchor, AfterOneCursor, (DateTimeOffset?)SortedFixture.At(1)));
        (end.Shard, end.Search, end.Cursor, end.IsExhausted, end.LastEmitted, end.CycleState, end.PreviousPageLastUpdated)
            .ShouldBe((Fixture.Shard(0), (ShardSearchRequest?)null, (ShardCursor?)null, true, (SortedAnchor?)null, CursorCycleState.Initial, (DateTimeOffset?)null));
    }
}

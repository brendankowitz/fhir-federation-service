using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>A continuation is valid by construction: its anchor belongs to the input it replays.</summary>
public sealed class UnsortedContinuationTests
{
    [Fact]
    public void AtStart_HoldsOnlyTheShard()
    {
        UnsortedContinuation start = UnsortedContinuation.AtStart(Fixture.Shard(1));

        start.Shard.ShouldBe(Fixture.Shard(1));
        start.Search.ShouldBeNull();
        start.Cursor.ShouldBeNull();
        start.LastEmitted.ShouldBeNull();
        start.CycleState.ShouldBe(CursorCycleState.Initial);
    }

    [Fact]
    public void AtSearch_KeepsTheSentRequestInstanceAndTheAnchor()
    {
        ShardSearchRequest sent = Fixture.Stream(1);

        UnsortedContinuation resume = UnsortedContinuation.AtSearch(sent, Fixture.Key(1, "a"));

        resume.Shard.ShouldBe(Fixture.Shard(1));
        resume.Search.ShouldBeSameAs(sent);
        resume.Cursor.ShouldBeNull();
        resume.LastEmitted.ShouldBe(Fixture.Key(1, "a"));
        resume.CycleState.ShouldBe(CursorCycleState.Initial);
    }

    [Fact]
    public void AtCursor_KeepsTheCursorAnOptionalAnchorAndTheCycleState()
    {
        ShardCursor cursor = Fixture.Cursor(1, "1");
        var cycle = new CursorCycleState(7UL, 4, 1);

        UnsortedContinuation resume = UnsortedContinuation.AtCursor(cursor, null, cycle);

        resume.Shard.ShouldBe(Fixture.Shard(1));
        resume.Search.ShouldBeNull();
        resume.Cursor.ShouldBe(cursor);
        resume.LastEmitted.ShouldBeNull();
        resume.CycleState.ShouldBeSameAs(cycle);
    }

    [Fact]
    public void Factories_RejectMissingInputs()
    {
        Should.Throw<ArgumentNullException>(() => UnsortedContinuation.AtStart(null!));
        Should.Throw<ArgumentNullException>(() => UnsortedContinuation.AtSearch(null!, Fixture.Key(0, "a")));
        Should.Throw<ArgumentNullException>(() => UnsortedContinuation.AtSearch(Fixture.Stream(0), null!));
        Should.Throw<ArgumentNullException>(() => UnsortedContinuation.AtCursor(null!, null, CursorCycleState.Initial));
        Should.Throw<ArgumentNullException>(() => UnsortedContinuation.AtCursor(Fixture.Cursor(0, "1"), null, null!));
    }

    [Fact]
    public void Factories_RejectAnAnchorThatTheInputCannotReturn()
    {
        Should.Throw<ArgumentException>(() => UnsortedContinuation.AtSearch(Fixture.Stream(0), Fixture.Key(1, "a")));
        Should.Throw<ArgumentException>(() => UnsortedContinuation.AtSearch(Fixture.Stream(0), Fixture.Key(0, "a", Fixture.Observation)));
        Should.Throw<ArgumentException>(() => UnsortedContinuation.AtCursor(Fixture.Cursor(0, "1"), Fixture.Key(1, "a"), CursorCycleState.Initial));
        Should.Throw<ArgumentException>(
            () => UnsortedContinuation.AtCursor(Fixture.Cursor(0, "1"), Fixture.Key(0, "a", Fixture.Observation), CursorCycleState.Initial));
    }

    [Fact]
    public void AtSearch_RejectsACountOnlyRequest()
    {
        var countOnly = new ShardSearchRequest(Fixture.Shard(0), Fixture.Patient, [new ParameterOccurrence("name", "x")], 0, ShardSearchMethod.Get);

        Should.Throw<ArgumentException>(() => UnsortedContinuation.AtSearch(countOnly, Fixture.Key(0, "a")));
    }
}

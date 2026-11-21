using System.Reflection;
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

    [Fact]
    public void Aggregate_RequiresTypeAndStreams()
    {
        Should.Throw<ArgumentNullException>(() => new SortedContinuation(null!, LastUpdatedSort.Ascending, 2, [Search(0)]))
            .ParamName.ShouldBe("type");
        Should.Throw<ArgumentNullException>(() => new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 2, null!))
            .ParamName.ShouldBe("streams");
    }

    [Fact]
    public void Aggregate_RejectsAnUndefinedDirectionOrANonpositivePageSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SortedContinuation(Fixture.Patient, (LastUpdatedSort)7, 2, [Search(0)]))
            .ParamName.ShouldBe("direction");
        Should.Throw<ArgumentOutOfRangeException>(() => new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 0, [Search(0)]))
            .ParamName.ShouldBe("pageSize");
    }

    public static TheoryData<string, SortedStreamContinuation?[]> MalformedStreams => new()
    {
        { "empty", [] },
        { "null member", [Search(0), null] },
        { "duplicate owner", [Search(0), SortedStreamContinuation.AtEnd(Fixture.Shard(0), null)] },
        { "all ended", [SortedStreamContinuation.AtEnd(Fixture.Shard(0), null), SortedStreamContinuation.AtEnd(Fixture.Shard(1), null)] },
        { "foreign search type", [Search(0), SortedStreamContinuation.AtSearch(SortedFixture.Stream(1, type: Fixture.Observation), null)] },
        { "foreign cursor type", [Search(0), SortedStreamContinuation.AtCursor(Fixture.Cursor(1, "p1", type: Fixture.Observation), null, AfterOneCursor, null)] },
        { "foreign ended anchor type", [Search(0), SortedStreamContinuation.AtEnd(Fixture.Shard(1), SortedFixture.Anchor(1, "a", 1, Fixture.Observation))] },
        { "preceding instant after its anchor", [Search(0), SortedStreamContinuation.AtCursor(Fixture.Cursor(1, "p1"), SortedFixture.Anchor(1, "b", 2), AfterOneCursor, SortedFixture.At(3))] },
    };

    [Theory]
    [MemberData(nameof(MalformedStreams))]
    public void Aggregate_RejectsMalformedStreams(string because, SortedStreamContinuation?[] streams)
    {
        Should.Throw<ArgumentException>(() => new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 2, streams!), because)
            .ParamName.ShouldBe("streams", because);
    }

    [Fact]
    public void Aggregate_OrdersAPrecedingInstantByTheSortDirection()
    {
        SortedStreamContinuation[] behindInDescending =
            [SortedStreamContinuation.AtCursor(Fixture.Cursor(0, "p1"), SortedFixture.Anchor(0, "b", 2), AfterOneCursor, SortedFixture.At(1))];
        SortedStreamContinuation[] equal =
            [SortedStreamContinuation.AtCursor(Fixture.Cursor(0, "p1"), SortedFixture.Anchor(0, "b", 2), AfterOneCursor, SortedFixture.At(2))];

        Should.Throw<ArgumentException>(() => new SortedContinuation(Fixture.Patient, LastUpdatedSort.Descending, 2, behindInDescending))
            .ParamName.ShouldBe("streams");
        new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 2, behindInDescending).Streams.Count.ShouldBe(1);
        new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 2, equal).Streams.Count.ShouldBe(1);
        new SortedContinuation(Fixture.Patient, LastUpdatedSort.Descending, 2, equal).Streams.Count.ShouldBe(1);
    }

    [Fact]
    public void Aggregate_IsAnImmutableSnapshotOfItsList()
    {
        SortedStreamContinuation first = Search(0);
        List<SortedStreamContinuation> streams = [first, SortedStreamContinuation.AtEnd(Fixture.Shard(1), null)];

        var continuation = new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 2, streams);
        streams[0] = Search(2);
        streams.Add(Search(3));

        continuation.Streams.Select(s => s.Shard.ToString()).ShouldBe(["s0", "s1"]);
        continuation.Streams[0].ShouldBeSameAs(first);
        IList<SortedStreamContinuation> downcast = continuation.Streams.ShouldBeAssignableTo<IList<SortedStreamContinuation>>();
        Should.Throw<NotSupportedException>(() => downcast[0] = Search(2));
        Should.Throw<NotSupportedException>(() => downcast.Clear());
    }

    /// <summary>
    /// Walks every field reachable from the snapshot types, through the federation's own types and the element types of
    /// arrays and generic collections, so a forbidden value held one or more levels down is found too.
    /// </summary>
    [Fact]
    public void Snapshot_HoldsOnlyMetadata_NoEntriesPagesBytesOrDeferredWork()
    {
        Type[] forbidden = [typeof(ShardEntry), typeof(ShardPage), typeof(byte[]), typeof(ReadOnlyMemory<byte>), typeof(Task), typeof(Delegate)];
        var pending = new Queue<Type>([typeof(SortedContinuation), typeof(SortedStreamContinuation), typeof(SortedAnchor)]);
        var visited = new HashSet<Type>();
        var walked = new List<string>();

        while (pending.TryDequeue(out Type? owner))
        {
            if (!visited.Add(owner))
            {
                continue;
            }

            walked.Add(owner.Name);
            foreach (FieldInfo field in owner.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (Type held in Held(field.FieldType))
                {
                    forbidden.ShouldNotContain(f => held.IsAssignableTo(f), $"{owner.Name}.{field.Name}");
                    if (held.Namespace?.StartsWith("Ignixa.", StringComparison.Ordinal) == true)
                    {
                        pending.Enqueue(held);
                    }
                }
            }
        }

        walked.ShouldContain(nameof(ShardSearchRequest));
        walked.ShouldContain(nameof(ShardCursor));
        walked.ShouldContain(nameof(CursorCycleState));
    }

    /// <summary>Gets a field's type and, recursively, the element and generic argument types it holds.</summary>
    private static IEnumerable<Type> Held(Type type) =>
        type.IsArray
            ? [type, .. Held(type.GetElementType()!)]
            : [type, .. type.GetGenericArguments().SelectMany(Held)];

    private static SortedStreamContinuation Search(int ordinal) =>
        SortedStreamContinuation.AtSearch(SortedFixture.Stream(ordinal, pageSize: 2), null);
}
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>Arguments the caller controls are rejected before any shard is called (design Q8).</summary>
public sealed class SortedPageExecutorArgumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Constructor_RejectsMissingDependenciesAndNoFetchSlot()
    {
        var client = new FakeShardClient();
        ShardRegistry registry = Fixture.Registry(1);

        Should.Throw<ArgumentNullException>(() => new SortedPageExecutor(null!, registry, 1)).ParamName.ShouldBe("client");
        Should.Throw<ArgumentNullException>(() => new SortedPageExecutor(client, null!, 1)).ParamName.ShouldBe("registry");
        Should.Throw<ArgumentOutOfRangeException>(() => new SortedPageExecutor(client, registry, 0)).ParamName.ShouldBe("maxParallelFetches");
    }

    [Theory]
    [InlineData(0, 10, LastUpdatedSort.Ascending, "pageSize")]
    [InlineData(-1, 10, LastUpdatedSort.Ascending, "pageSize")]
    [InlineData(5, -1, LastUpdatedSort.Ascending, "fetchBudget")]
    [InlineData(5, 10, (LastUpdatedSort)7, "direction")]
    public async Task ExecuteAsync_RejectsOutOfRangeSizesAndDirection_BeforeAnyCall(
        int pageSize,
        int fetchBudget,
        LastUpdatedSort direction,
        string paramName)
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ArgumentOutOfRangeException error = await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => executor.ExecuteAsync(SortedFixture.Streams(direction, 0, 1), direction, pageSize, null, fetchBudget, Token));

        error.GetType().ShouldBe(typeof(ArgumentOutOfRangeException));
        error.ParamName.ShouldBe(paramName);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMissingStreams_BeforeAnyCall()
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ArgumentNullException error = await Should.ThrowAsync<ArgumentNullException>(
            () => executor.ExecuteAsync(null!, LastUpdatedSort.Ascending, 5, null, 10, Token));

        error.ParamName.ShouldBe("streams");
        client.Calls.ShouldBeEmpty();
    }

    public static TheoryData<string> InvalidStreamSets => ["empty", "null member", "unknown shard", "repeated shard", "mixed type"];

    [Theory]
    [MemberData(nameof(InvalidStreamSets))]
    public async Task ExecuteAsync_RejectsInvalidStreamSets_BeforeAnyCall(string invalid)
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = invalid switch
        {
            "empty" => [],
            "null member" => [SortedFixture.Stream(0), null!],
            "unknown shard" => [SortedFixture.Stream(0), SortedFixture.Stream(5)],
            "repeated shard" => [SortedFixture.Stream(1), SortedFixture.Stream(1)],
            _ => [SortedFixture.Stream(0), SortedFixture.Stream(1, type: Fixture.Observation)],
        };

        ArgumentException error = await Should.ThrowAsync<ArgumentException>(
            () => executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 5, null, 10, Token));

        error.GetType().ShouldBe(typeof(ArgumentException));
        error.ParamName.ShouldBe("streams");
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ObservesCancellation_BeforeAnyCall()
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 5, null, 10, cancelled.Token));

        client.Calls.ShouldBeEmpty();
    }

    public static TheoryData<string> ForeignSnapshots =>
    [
        "type", "direction", "page size", "missing owner", "extra owner", "owner order", "search method",
        "search parameter value", "search parameter order", "search count above the owner limit", "cursor redemption",
    ];

    [Theory]
    [MemberData(nameof(ForeignSnapshots))]
    public async Task ExecuteAsync_RejectsASnapshotOfAnotherPlan_BeforeAnyCall(string foreign)
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(4, 2));
        ShardSearchRequest[] streams = PlanStreams();
        SortedStreamContinuation[] owners = PlanOwners();
        (SortedStreamContinuation s0, SortedStreamContinuation s1, SortedStreamContinuation s2) = (owners[0], owners[1], owners[2]);
        SortedContinuation resumeFrom = foreign switch
        {
            "type" => new(Fixture.Observation, LastUpdatedSort.Ascending, 5, [
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, type: Fixture.Observation), null),
                SortedStreamContinuation.AtEnd(Fixture.Shard(1), null),
                SortedStreamContinuation.AtEnd(Fixture.Shard(2), null)]),
            "direction" => new(Fixture.Patient, LastUpdatedSort.Descending, 5, [s0, s1, s2]),
            "page size" => new(Fixture.Patient, LastUpdatedSort.Ascending, 6, [s0, s1, s2]),
            "missing owner" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [s0, s1]),
            "extra owner" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [s0, s1, s2, SortedStreamContinuation.AtEnd(Fixture.Shard(3), null)]),
            "owner order" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [s1, s0, s2]),
            "search method" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, method: ShardSearchMethod.PostSearch), null), s1, s2]),
            "search parameter value" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [
                SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, LastUpdatedSort.Descending), null), s1, s2]),
            "search parameter order" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [
                SortedStreamContinuation.AtSearch(
                    new ShardSearchRequest(
                        Fixture.Shard(0),
                        Fixture.Patient,
                        [SortedFixture.SortOccurrence(LastUpdatedSort.Ascending), new ParameterOccurrence("name", Fixture.SecretValue)],
                        13,
                        ShardSearchMethod.Get),
                    null),
                s1,
                s2]),
            "search count above the owner limit" => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [
                s0, s1, SortedStreamContinuation.AtSearch(SortedFixture.Stream(2, method: ShardSearchMethod.PostSearch, pageSize: 3), null)]),
            _ => new(Fixture.Patient, LastUpdatedSort.Ascending, 5, [
                s0, s1, SortedStreamContinuation.AtCursor(Fixture.Cursor(2, "p1"), null, new CursorCycleState(1UL, 2, 0), null)]),
        };

        ArgumentException error = await Should.ThrowAsync<ArgumentException>(
            () => executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 5, resumeFrom, 10, Token));

        error.GetType().ShouldBe(typeof(ArgumentException));
        error.ParamName.ShouldBe("resumeFrom");
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_AcceptsASnapshotOfItsOwnPlan_FromStructurallyEqualRequests()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "c", 3)));
        client.OnContinue(Fixture.Cursor(1, "p1"), Fixture.Page(null, SortedFixture.Entry(1, "b", 2), SortedFixture.Entry(1, "d", 4)));
        client.OnContinue(Fixture.Cursor(2, "p1", NextLinkRedemption.RepostToSearch), Fixture.Page(null, SortedFixture.Entry(2, "e", 5)));
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(4, 2));

        SortedPage page = await executor.ExecuteAsync(
            PlanStreams(), LastUpdatedSort.Ascending, 5, new SortedContinuation(Fixture.Patient, LastUpdatedSort.Ascending, 5, PlanOwners()), 10, Token);

        page.Ids().ShouldBe(["s0/c", "s1/d", "s2/e"]);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=13", "continue s1", "continue s2"]);
    }

    /// <summary>The plan the snapshot tests redeem against: s2 is posted and accepts at most 2 entries a page.</summary>
    private static ShardSearchRequest[] PlanStreams() =>
        [SortedFixture.Stream(0), SortedFixture.Stream(1), SortedFixture.Stream(2, method: ShardSearchMethod.PostSearch)];

    /// <summary>A well-formed checkpoint of every <see cref="PlanStreams"/> owner, each built apart from the plan.</summary>
    private static SortedStreamContinuation[] PlanOwners() =>
    [
        SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, pageSize: 13), SortedFixture.Anchor(0, "a", 1)),
        SortedStreamContinuation.AtCursor(Fixture.Cursor(1, "p1"), SortedFixture.Anchor(1, "b", 2), new CursorCycleState(1UL, 2, 0), null),
        SortedStreamContinuation.AtCursor(
            Fixture.Cursor(2, "p1", NextLinkRedemption.RepostToSearch), null, new CursorCycleState(1UL, 2, 0), null),
    ];
}
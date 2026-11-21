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

    [Fact]
    public async Task ExecuteAsync_ResumeIsNotYetSupported_AndFailsBeforeAnyCall()
    {
        var client = new FakeShardClient();
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));
        ShardSearchRequest[] streams = SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1);
        var resumeFrom = new SortedContinuation(
            Fixture.Patient,
            LastUpdatedSort.Ascending,
            5,
            [SortedStreamContinuation.AtSearch(streams[0], null), SortedStreamContinuation.AtSearch(streams[1], null)]);

        await Should.ThrowAsync<NotSupportedException>(
            () => executor.ExecuteAsync(streams, LastUpdatedSort.Ascending, 5, resumeFrom, 10, Token));

        client.Calls.ShouldBeEmpty();
    }
}

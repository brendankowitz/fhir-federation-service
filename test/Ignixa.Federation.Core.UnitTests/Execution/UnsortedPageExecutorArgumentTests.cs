using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Arguments the caller controls are rejected before any shard is called.</summary>
public sealed class UnsortedPageExecutorArgumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Constructor_RejectsMissingDependenciesAndNoFetchSlot()
    {
        var client = new FakeShardClient();
        ShardRegistry registry = Fixture.Registry(1);

        Should.Throw<ArgumentNullException>(() => new UnsortedPageExecutor(null!, registry, 1));
        Should.Throw<ArgumentNullException>(() => new UnsortedPageExecutor(client, null!, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new UnsortedPageExecutor(client, registry, 0));
    }

    [Fact]
    public async Task ExecuteAsync_RejectsInvalidSizesWithoutCallingAShard()
    {
        var client = new FakeShardClient();
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(1));

        await Should.ThrowAsync<ArgumentNullException>(executor.ExecuteAsync(null!, 1, null, 1, Token));
        await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync([], 1, null, 1, Token));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(executor.ExecuteAsync(Fixture.Streams(1), 0, null, 1, Token));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(executor.ExecuteAsync(Fixture.Streams(1), 1, null, -1, Token));
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RejectsStreamsThatAreNotOneTypeOnDistinctRegisteredShards()
    {
        var client = new FakeShardClient();
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(2));

        await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync([Fixture.Stream(0), Fixture.Stream(5)], 1, null, 1, Token));
        await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync([Fixture.Stream(0), Fixture.Stream(0)], 1, null, 1, Token));
        await Should.ThrowAsync<ArgumentException>(
            executor.ExecuteAsync([Fixture.Stream(0), Fixture.Stream(1, type: Fixture.Observation)], 1, null, 1, Token));
        await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync([Fixture.Stream(0), null!], 1, null, 1, Token));
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_RejectsAContinuationThatDoesNotMatchItsStream()
    {
        var client = new FakeShardClient();
        UnsortedPageExecutor executor = Fixture.Executor(client, Fixture.Registry(3));
        ShardSearchRequest[] streams = [Fixture.Stream(0), Fixture.Stream(1)];
        var postSent = new ShardSearchRequest(
            Fixture.Shard(1), Fixture.Patient, [new ParameterOccurrence("name", "x")], 3, ShardSearchMethod.PostSearch);

        UnsortedContinuation[] mismatched =
        [
            UnsortedContinuation.AtStart(Fixture.Shard(2)),
            UnsortedContinuation.AtSearch(postSent, Fixture.Key(1, "a")),
            UnsortedContinuation.AtCursor(Fixture.Cursor(1, "1", NextLinkRedemption.RepostToSearch), null, CursorCycleState.Initial),
            UnsortedContinuation.AtCursor(Fixture.Cursor(1, "1", type: Fixture.Observation), null, CursorCycleState.Initial),
        ];

        foreach (UnsortedContinuation resume in mismatched)
        {
            await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync(streams, 1, resume, 1, Token));
        }

        client.Calls.ShouldBeEmpty();
    }
}

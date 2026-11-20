using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Needed failures and backend contract violations fail the page (design spec §7.8, §8).</summary>
public sealed class UnsortedPageExecutorFailureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NeededShardThrows_FailsThePageWithTheShardAndTheClientException()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);
        var failure = new InvalidOperationException("backend said 500");
        client.OnSearchThrows(1, failure);

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 5, null, 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(1));
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        thrown.InnerException.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task ClientCancelsWhileTheCallerDoesNot_IsAShardTimeout()
    {
        var client = new FakeShardClient();
        client.OnSearchThrows(0, new TaskCanceledException("backend call timed out"));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(1)).ExecuteAsync(Fixture.Streams(1), 5, null, 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Reason.ShouldBe(ShardFailureReason.Timeout);
        thrown.InnerException.ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task CallerCancelsANeededCall_PropagatesOperationCanceled()
    {
        var client = new FakeShardClient();
        var gate = new TaskCompletionSource();
        client.OnSearch(0, gate, Fixture.Page(null, Fixture.Entries(0, "a")));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(1)).ExecuteAsync(Fixture.Streams(1), 5, null, 10, caller.Token);
        await client.WhenStarted(1);
        await caller.CancelAsync();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task DuplicateKeyInANeededPage_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a", "b", "a")));

        await ShouldViolateContract(client, Fixture.Shard(0));
    }

    [Fact]
    public async Task EntryFromAnotherShard_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, [.. Fixture.Entries(0, "a"), .. Fixture.Entries(1, "b")]));

        await ShouldViolateContract(client, Fixture.Shard(0));
    }

    [Fact]
    public async Task EntryOfAnotherType_IsAContractViolation()
    {
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entry(Fixture.Key(0, "a", Fixture.Observation))));

        await ShouldViolateContract(client, Fixture.Shard(0));
    }

    [Fact]
    public async Task SelfLink_IsAContractViolation()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a")));
        client.OnContinue(cursor, Fixture.Page(cursor, Fixture.Entries(0, "b")));

        await ShouldViolateContract(client, Fixture.Shard(0));
    }

    [Fact]
    public async Task CursorCycle_IsAContractViolation()
    {
        var client = new FakeShardClient();
        ShardCursor first = Fixture.Cursor(0, "1");
        ShardCursor second = Fixture.Cursor(0, "2");
        client.OnSearch(0, Fixture.Page(first, Fixture.Entries(0, "a")));
        client.OnContinue(first, Fixture.Page(second));
        client.OnContinue(second, Fixture.Page(first, Fixture.Entries(0, "b")));

        await ShouldViolateContract(client, Fixture.Shard(0));
        client.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task SelfLinkOnAnExactFillOfAResumedCursor_IsAContractViolation()
    {
        var client = new FakeShardClient();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnContinue(cursor, Fixture.Page(cursor, Fixture.Entries(0, "a", "b")));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(1))
                .ExecuteAsync(Fixture.Streams(1), 2, UnsortedContinuation.AtCursor(cursor, null, CursorCycleState.Initial), 10, Token));

        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Theory]
    [InlineData("shard")]
    [InlineData("type")]
    [InlineData("redemption")]
    public async Task SuccessorCursorThatDoesNotBelongToTheStream_IsAContractViolation(string wrong)
    {
        // The stream starts with POST on a RepostToSearch profile, so its cursors must be RepostToSearch.
        ShardCursor successor = wrong switch
        {
            "shard" => Fixture.Cursor(1, "1", NextLinkRedemption.RepostToSearch),
            "type" => Fixture.Cursor(0, "1", NextLinkRedemption.RepostToSearch, Fixture.Observation),
            _ => Fixture.Cursor(0, "1", NextLinkRedemption.FollowGet),
        };
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(successor, Fixture.Entries(0, "a")));
        client.OnContinue(successor, Fixture.Page(null, Fixture.Entries(0, "b")));
        ShardSearchRequest[] streams = [Fixture.Stream(0, ShardSearchMethod.PostSearch), Fixture.Stream(1, ShardSearchMethod.PostSearch)];

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(streams, 5, null, 10, Token));

        thrown.Shard.ShouldBe(Fixture.Shard(0));
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        thrown.Message.ShouldNotContain(successor.Target);
        thrown.Message.ShouldNotContain(Fixture.SecretValue);
        client.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SuccessorCursorThatDoesNotBelongToTheStream_IsNotReturnedOnAnExactFill()
    {
        ShardCursor successor = Fixture.Cursor(0, "1", NextLinkRedemption.RepostToSearch);
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(successor, Fixture.Entries(0, "a")));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(1)).ExecuteAsync(Fixture.Streams(1), 1, null, 10, Token));

        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    private static async Task ShouldViolateContract(FakeShardClient client, ShardId shard)
    {
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Fixture.Executor(client, Fixture.Registry(2)).ExecuteAsync(Fixture.Streams(2), 5, null, 10, Token));

        thrown.Shard.ShouldBe(shard);
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        thrown.InnerException.ShouldBeNull();
    }
}

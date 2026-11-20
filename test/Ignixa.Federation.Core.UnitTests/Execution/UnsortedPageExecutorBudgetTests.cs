using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>The live shard-call budget, with the frontier's first claim on it (design spec §7.8).</summary>
public sealed class UnsortedPageExecutorBudgetTests
{
    /// <summary>Runs each timing-sensitive test 20 times.</summary>
    public static TheoryData<int> Iterations => [.. Enumerable.Range(1, 20)];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoBudget_ThrowsWithoutCallingAShard()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a"]);

        await Should.ThrowAsync<FetchBudgetExhaustedException>(
            Fixture.Executor(client, Fixture.Registry(1)).ExecuteAsync(Fixture.Streams(1), 5, null, 0, Token));

        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NeededCallBeyondTheBudget_Throws()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b", "c", "d", "e", "f"], profile: Fixture.SmallPages);

        FetchBudgetExhaustedException thrown = await Should.ThrowAsync<FetchBudgetExhaustedException>(
            Fixture.Executor(client, Fixture.Registry(1, 0)).ExecuteAsync(Fixture.Streams(1), 6, null, 2, Token));

        client.Calls.Count.ShouldBe(2);
        thrown.Message.ShouldContain("2");
    }

    [Fact]
    public async Task PageCompletesWithinTheBudget_ReportsTheCallsStarted()
    {
        var client = new FakeShardClient();
        client.Serve(0, ["a", "b", "c", "d", "e", "f"], profile: Fixture.SmallPages);

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(1, 0)).ExecuteAsync(Fixture.Streams(1), 6, null, 3, Token);

        page.Matches.Count.ShouldBe(6);
        page.Fetches.ShouldBe(3);
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task BudgetWithOneCallLeft_IsKeptForTheFrontier(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(null, Fixture.Entries(0, "a")));
        client.OnSearch(1, Fixture.Page(null, Fixture.Entries(1, "b", "c")));
        client.OnSearch(2, Fixture.Page(null, Fixture.Entries(2, "x")));

        UnsortedPage page = await Fixture.Executor(client, Fixture.Registry(3), 4).ExecuteAsync(Fixture.Streams(3), 3, null, 2, Token);

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a"), .. Fixture.Ids(1, "b", "c")]);
        page.Fetches.ShouldBe(2);
        client.Calls.ShouldNotContain(call => call.Shard == Fixture.Shard(2));
    }

    [Theory]
    [MemberData(nameof(Iterations))]
    public async Task Speculation_NeverSpendsTheFrontiersLastCall(int iteration)
    {
        _ = iteration;
        var client = new FakeShardClient();
        var gate = new TaskCompletionSource();
        ShardCursor cursor = Fixture.Cursor(0, "1");
        client.OnSearch(0, Fixture.Page(cursor, Fixture.Entries(0, "a")));
        client.OnContinue(cursor, gate, Fixture.Page(null, Fixture.Entries(0, "b")));
        client.OnSearch(1, Fixture.Page(null, Fixture.Entries(1, "c")));
        client.OnSearch(2, Fixture.Page(null, Fixture.Entries(2, "x")));

        Task<UnsortedPage> execution = Fixture.Executor(client, Fixture.Registry(3), 4).ExecuteAsync(Fixture.Streams(3), 3, null, 3, Token);
        await client.WhenStarted(2);
        gate.SetResult();
        UnsortedPage page = await execution;

        page.Ids().ShouldBe([.. Fixture.Ids(0, "a", "b"), .. Fixture.Ids(1, "c")]);
        page.Fetches.ShouldBe(3);
        client.Calls.ShouldNotContain(call => call.Shard == Fixture.Shard(2));
    }
}

using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// Pages that break the shard contract fail the call as a typed violation before any of their entries is used, even when
/// the page overflows the include limit; no failure repeats a cursor or a resource.
/// </summary>
public sealed class RevIncludePageExecutorContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // A cursor already redeemed by this stream closes a cycle, even through empty pages, and even on a page whose entries
    // would overflow the limit.
    [Theory]
    [InlineData("self", 10)]
    [InlineData("self", 0)]
    [InlineData("cycle", 10)]
    [InlineData("cycle", 0)]
    public async Task ARepeatedCursorIsAContractViolation(string shape, int includeLimit)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        ShardCursor third = Cursor(registry, East, Observation, "3");
        backend.OnSearch(East, Observation, Page(second));
        if (shape == "self")
        {
            backend.OnContinue(second, Page(second, Entry(East, Observation, "o1")));
        }
        else
        {
            backend.OnContinue(second, Page(third));
            backend.OnContinue(third, Page(second, Entry(East, Observation, "o1")));
        }

        backend.OnSearch(West, Observation, Page(null));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, includeLimit, 10, Token));

        thrown.Shard.ShouldBe(East);
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        thrown.ToString().ShouldNotContain("sarah-secret");
        backend.Client.Calls.Count(call => call.Cursor == second).ShouldBe(1);
    }

    public static TheoryData<string> ForeignCursors => ["owner", "type", "redemption"];

    [Theory]
    [MemberData(nameof(ForeignCursors))]
    public async Task ANextOfAnotherStreamIsAContractViolationOnAnOverflowingPage(string foreign)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor own = Cursor(registry, East, Observation, "2");
        ShardCursor next = foreign switch
        {
            "owner" => Cursor(registry, West, Observation, "2"),
            "type" => Cursor(registry, East, Encounter, "2"),
            _ => new ShardCursor(own.Shard, own.Type, own.Target, Enum.GetValues<NextLinkRedemption>().First(r => r != own.Redemption)),
        };
        backend.OnSearch(East, Observation, Page(next, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 0, 10, Token));

        thrown.Shard.ShouldBe(East);
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        thrown.ToString().ShouldNotContain("sarah-secret");
        backend.Client.Calls.ShouldAllBe(call => call.Search != null);
    }

    public static TheoryData<string> ForeignEntries => ["owner", "type", "repeated"];

    [Theory]
    [MemberData(nameof(ForeignEntries))]
    public async Task AnEntryTheStreamCannotReturnIsAContractViolationOnAnOverflowingPage(string foreign)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardEntry[] entries = foreign switch
        {
            "owner" => [Entry(East, Observation, "o1"), Entry(West, Observation, "o2")],
            "type" => [Entry(East, Observation, "o1"), Entry(East, Encounter, "e1")],
            _ => [Entry(East, Observation, "o1", "1"), Entry(East, Observation, "o1", "2")],
        };
        backend.OnSearch(East, Observation, Page(null, entries));
        backend.OnSearch(West, Observation, Page(null));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 0, 10, Token));

        thrown.Shard.ShouldBe(East);
        thrown.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }
}

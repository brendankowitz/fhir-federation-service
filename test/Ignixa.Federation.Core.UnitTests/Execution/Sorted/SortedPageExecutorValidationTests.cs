using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>Which shard pages a sorted execution refuses to merge, and how it fails (design Q5 and Q8).</summary>
public sealed class SortedPageExecutorValidationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LastUpdatedSort.Ascending)]
    [InlineData(LastUpdatedSort.Descending)]
    public async Task OneTickReversalInAnUnusedTail_IsAContractViolation(LastUpdatedSort direction)
    {
        int sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        DateTimeOffset tail = SortedFixture.At(sign * 5);
        var client = new FakeShardClient();
        client.OnSearch(0, Fixture.Page(
            null,
            SortedFixture.Entry(0, "a", sign * 1),
            SortedFixture.Entry(0, "b", sign * 2),
            SortedFixture.Entry(0, "c", tail),
            SortedFixture.Entry(0, "d", tail.AddTicks(-sign))));
        client.Chain(1, [SortedFixture.Entry(1, "x", sign * 3)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => executor.ExecuteAsync(
            SortedFixture.Streams(direction, 0, 1), direction, 2, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        failure.InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending)]
    [InlineData(LastUpdatedSort.Descending)]
    public async Task NextPageStartingBehindThePriorTail_AcrossAnEmptyPage_IsAContractViolation(LastUpdatedSort direction)
    {
        int sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        var client = new FakeShardClient();
        client.Chain(
            0,
            [SortedFixture.Entry(0, "a", sign * 1), SortedFixture.Entry(0, "b", sign * 5)],
            [],
            [SortedFixture.Entry(0, "c", sign * 4), SortedFixture.Entry(0, "d", sign * 6)]);
        client.Chain(1, [SortedFixture.Entry(1, "x", sign * 10)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => executor.ExecuteAsync(
            SortedFixture.Streams(direction, 0, 1), direction, 3, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Theory]
    [InlineData(LastUpdatedSort.Ascending, "search")]
    [InlineData(LastUpdatedSort.Descending, "search")]
    [InlineData(LastUpdatedSort.Ascending, "cursor")]
    [InlineData(LastUpdatedSort.Descending, "cursor")]
    public async Task ReversalBeforeTheReplayedAnchor_IsAContractViolation_ThoughAnchorAndSuffixAreValid(
        LastUpdatedSort direction,
        string input)
    {
        int sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        ShardCursor p1 = Fixture.Cursor(0, "p1");
        SortedAnchor anchor = SortedFixture.Anchor(0, "c", sign * 3);
        var client = new FakeShardClient();

        // A search reverses inside its skipped prefix; a cursor's first entry falls behind its saved preceding instant.
        client.OnSearch(0, Fixture.Page(
            null,
            SortedFixture.Entry(0, "a", sign * 2),
            SortedFixture.Entry(0, "b", sign * 1),
            SortedFixture.Entry(0, "c", sign * 3),
            SortedFixture.Entry(0, "d", sign * 4)));
        client.OnContinue(p1, Fixture.Page(
            null,
            SortedFixture.Entry(0, "b", sign * 1),
            SortedFixture.Entry(0, "c", sign * 3),
            SortedFixture.Entry(0, "d", sign * 4)));
        SortedStreamContinuation start = input == "search"
            ? SortedStreamContinuation.AtSearch(SortedFixture.Stream(0, direction, pageSize: 4), anchor)
            : SortedStreamContinuation.AtCursor(p1, anchor, new CursorCycleState(1UL, 2, 0), SortedFixture.At(sign * 2));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => client.Resume(direction, 2, start));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task NullLastUpdatedOnANeededPage_IsAContractViolationBeforeAnyEmission(int nullPosition)
    {
        string[] ids = ["a", "b", "c"];
        ShardEntry[] entries =
        [
            .. ids.Select((id, i) => SortedFixture.Entry(1, id, i == nullPosition ? null : SortedFixture.At(i + 1))),
        ];
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "x", 0)]);
        client.Chain(1, entries);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 1, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(1));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Fact]
    public async Task ProfileWithoutOrderMetadata_IsNotARuntimeFailure()
    {
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 3)]);
        client.Chain(1, [SortedFixture.Entry(1, "x", 2)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2, 0));

        SortedPage page = await executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 5, null, 10, Token);

        page.Ids().ShouldBe(["s0/a", "s1/x", "s0/b"]);
        page.Next.ShouldBeNull();
    }

    [Theory]
    [InlineData("foreign shard")]
    [InlineData("foreign type")]
    [InlineData("duplicate key")]
    public async Task ForeignOrDuplicateKeyInAnUnusedTail_IsAContractViolation(string defect)
    {
        ShardEntry bad = defect switch
        {
            "foreign shard" => SortedFixture.Entry(1, "f", 2),
            "foreign type" => new ShardEntry(Fixture.Key(0, "f", Fixture.Observation), "v", SortedFixture.At(2), "{}"u8.ToArray()),
            _ => SortedFixture.Entry(0, "a", 2),
        };
        var client = new FakeShardClient();
        client.Chain(0, [SortedFixture.Entry(0, "a", 1), bad]);
        client.Chain(1, [SortedFixture.Entry(1, "x", 5)]);
        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(2));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0, 1), LastUpdatedSort.Ascending, 1, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Theory]
    [InlineData("foreign shard")]
    [InlineData("foreign type")]
    [InlineData("wrong redemption")]
    [InlineData("self link")]
    [InlineData("repeated cursor")]
    public async Task BadNextOnThePageThatExactlyFillsThePage_IsAContractViolation(string defect)
    {
        var client = new FakeShardClient();
        ShardEntry[] last;
        ShardCursor bad;
        int pagesNeeded;
        switch (defect)
        {
            case "self link":
                ShardCursor[] self = client.Chain(0, [SortedFixture.Entry(0, "a", 1)], []);
                last = [SortedFixture.Entry(0, "b", 2), SortedFixture.Entry(0, "c", 3)];
                bad = self[0];
                pagesNeeded = 2;
                client.OnContinue(self[0], Fixture.Page(bad, last));
                break;
            case "repeated cursor":
                ShardCursor[] chain = client.Chain(0, [SortedFixture.Entry(0, "a", 1)], [SortedFixture.Entry(0, "b", 2)], []);
                last = [SortedFixture.Entry(0, "c", 3)];
                bad = chain[0];
                pagesNeeded = 3;
                client.OnContinue(chain[1], Fixture.Page(bad, last));
                break;
            default:
                last = [SortedFixture.Entry(0, "a", 1), SortedFixture.Entry(0, "b", 2), SortedFixture.Entry(0, "c", 3)];
                bad = defect switch
                {
                    "foreign shard" => Fixture.Cursor(1, "p1"),
                    "foreign type" => Fixture.Cursor(0, "p1", type: Fixture.Observation),
                    _ => Fixture.Cursor(0, "p1", NextLinkRedemption.RepostToSearch),
                };
                client.OnSearch(0, Fixture.Page(bad, last));
                pagesNeeded = 1;
                break;
        }

        SortedPageExecutor executor = SortedFixture.Executor(client, Fixture.Registry(1));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => executor.ExecuteAsync(
            SortedFixture.Streams(LastUpdatedSort.Ascending, 0), LastUpdatedSort.Ascending, 3, null, 10, Token));

        failure.Shard.ShouldBe(Fixture.Shard(0));
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        client.Calls.Count.ShouldBe(pagesNeeded, "the bad link is never redeemed");
    }
}

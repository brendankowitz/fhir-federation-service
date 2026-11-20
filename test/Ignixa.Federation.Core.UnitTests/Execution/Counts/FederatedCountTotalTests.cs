using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>How shard totals become the federated total, and which shard responses are refused.</summary>
public sealed class FederatedCountTotalTests
{
    private readonly SearchCapabilities _capabilities = Certified("Patient?name");

    private QueryPlan CountPlan => Plan(_capabilities, "Patient", "name=x&_count=0");

    [Fact]
    public async Task ProvenEmptyShardContributesZeroWithoutACall()
    {
        var client = new FakeShardClient();
        client.Answer(West, 9);
        ShardCountTarget[] targets = [ShardCountTarget.ProvenEmpty(East), ShardCountTarget.Search(West, ShardSearchMethod.Get, NoReplacements)];

        int? total = await Executor(client, _capabilities).ExecuteAsync(CountPlan, targets, 1, Token);

        total.ShouldBe(9);
        client.Calls.Select(c => c.Shard).ShouldBe([West]);
    }

    [Fact]
    public async Task EveryShardProvenEmptyIsZeroWithoutCalls()
    {
        var client = new FakeShardClient();
        ShardCountTarget[] targets = [ShardCountTarget.ProvenEmpty(West), ShardCountTarget.ProvenEmpty(East)];

        int? total = await Executor(client, _capabilities).ExecuteAsync(CountPlan, targets, 0, Token);

        total.ShouldBe(0);
        client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0L, 0L, 0)]
    [InlineData(int.MaxValue, 0L, int.MaxValue)]
    [InlineData(int.MaxValue - 1L, 1L, int.MaxValue)]
    public async Task SumsExactly(long east, long west, int expected)
    {
        var client = new FakeShardClient();
        client.Answer(East, east);
        client.Answer(West, west);

        int? total = await Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token);

        total.ShouldBe(expected);
    }

    [Fact]
    public async Task SumAboveTheLargestBundleTotalIsRefusedNotTruncated()
    {
        var client = new FakeShardClient();
        client.Answer(East, int.MaxValue);
        client.Answer(West, 1);

        await Should.ThrowAsync<TotalOverflowException>(
            () => Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token));
    }

    [Fact]
    public async Task ShardTotalAboveTheLargestBundleTotalIsAContractViolation()
    {
        var client = new FakeShardClient();
        client.Answer(East, int.MaxValue + 1L);
        client.Answer(West, 0);

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Fact]
    public async Task SameResourceIdsOnTwoShardsBothCount()
    {
        // Identities are server-scoped (§5): east's Patient/1 and west's Patient/1 are different resources.
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);

        int? total = await Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token);

        total.ShouldBe(2);
    }

    public static TheoryData<string> BrokenResponses => ["missing total", "entries", "next link"];

    [Theory]
    [MemberData(nameof(BrokenResponses))]
    public async Task ResponseThatIsNotAnExactCountIsAContractViolation(string broken)
    {
        ShardPage page = broken switch
        {
            "missing total" => Count(null),
            "entries" => new ShardPage([Fixture.Entry(new ResourceKey(West, Fixture.Patient, ResourceId.Parse("p1")))], null, 1),
            _ => new ShardPage([], new ShardCursor(West, Fixture.Patient, "https://west.example/fhir/Patient?ct=2", NextLinkRedemption.FollowGet), 1),
        };
        var client = new FakeShardClient();
        client.Answer(East, 5);
        client.OnSearch(West, (_, _) => Task.FromResult(page));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token));

        failure.Shard.ShouldBe(West);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        failure.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task ClientFailureFailsTheCountWithoutAPartialTotal()
    {
        var cause = new HttpRequestException("west is down");
        var client = new FakeShardClient();
        client.Answer(East, 5);
        client.OnSearch(West, (_, _) => Task.FromException<ShardPage>(cause));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token));

        failure.Shard.ShouldBe(West);
        failure.Reason.ShouldBe(ShardFailureReason.Failed);
        failure.InnerException.ShouldBeSameAs(cause);
    }

    [Fact]
    public async Task CancellationTheCallerDidNotAskForIsATimeout()
    {
        var timeout = new OperationCanceledException("client timeout");
        var client = new FakeShardClient();
        client.Answer(East, 5);
        client.OnSearch(West, (_, _) => Task.FromException<ShardPage>(timeout));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(
            () => Executor(client, _capabilities).ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, Token));

        failure.Shard.ShouldBe(West);
        failure.Reason.ShouldBe(ShardFailureReason.Timeout);
        failure.InnerException.ShouldBeSameAs(timeout);
    }
}

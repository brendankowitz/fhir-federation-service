using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>Every refusal happens before the first shard call.</summary>
public sealed class FederatedCountAdmissionTests
{
    private readonly SearchCapabilities _capabilities = Certified("Patient?name");

    private readonly FakeShardClient _client = Answering();

    private QueryPlan CountPlan => Plan(_capabilities, "Patient", "name=x&_count=0");

    private FederatedCountExecutor Executor() => CountFixture.Executor(_client, _capabilities);

    [Fact]
    public async Task ShapeNotCertifiedByEveryShardIsRefused()
    {
        SearchCapabilities stale = Capabilities(
            Certification(exactCountShapes: ["Patient?name"]),
            Certification(exactCountShapes: ["Patient?gender"]));

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => CountFixture.Executor(_client, stale).ExecuteAsync(CountPlan, SearchEvery(stale), 2, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ShardAddedSinceThePlanThatDoesNotCertifyTheShapeIsRefused()
    {
        ShardRegistry registry = Fixture.Registry(3);
        SearchCertification certified = Certification(exactCountShapes: ["Patient?name"]);
        var wider = new SearchCapabilities(
            registry,
            [
                new ShardSearchCapability(Fixture.Shard(0), AllResources, certified),
                new ShardSearchCapability(Fixture.Shard(1), AllResources, certified),
                new ShardSearchCapability(Fixture.Shard(2), AllResources, Certification()),
            ]);

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => CountFixture.Executor(_client, wider).ExecuteAsync(CountPlan, SearchEvery(wider), 3, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TypeNotEveryShardSearchesIsRefused()
    {
        SearchCapabilities narrow = Capabilities(
            Certification(exactCountShapes: ["Patient?name"]),
            Certification(exactCountShapes: ["Patient?name"]),
            westResources: [.. AllResources.Skip(1)]);

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => CountFixture.Executor(_client, narrow).ExecuteAsync(CountPlan, SearchEvery(narrow), 2, Token));

        _client.Calls.ShouldBeEmpty();
    }

    public static TheoryData<string> BadTargets => ["missing", "duplicate", "unknown", "null"];

    [Theory]
    [MemberData(nameof(BadTargets))]
    public async Task TargetsMustCoverEveryRegisteredShardExactlyOnce(string bad)
    {
        ShardCountTarget east = ShardCountTarget.Search(East, ShardSearchMethod.Get, NoReplacements);
        ShardCountTarget[] targets = bad switch
        {
            "missing" => [east],
            "duplicate" => [east, east, ShardCountTarget.ProvenEmpty(West)],
            "unknown" => [east, ShardCountTarget.ProvenEmpty(West), ShardCountTarget.ProvenEmpty(ShardId.Parse("north"))],
            _ => [east, ShardCountTarget.ProvenEmpty(West), null!],
        };

        await Should.ThrowAsync<ArgumentException>(() => Executor().ExecuteAsync(CountPlan, targets, 2, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnresolvedChainReplacementIsRefusedBeforeAnyCall()
    {
        SearchCertification certified = Certification(
            exactCountShapes: ["Observation?subject=,"],
            paths: [FullPath(ObservationSubjectPatient, forwardShapes: ["count:Observation?@Observation.subject:Patient"])]);
        SearchCapabilities capabilities = Capabilities(certified, certified);
        QueryPlan plan = Plan(capabilities, "Observation", "subject:Patient.name=x&_summary=count");
        ShardCountTarget[] targets =
        [
            ShardCountTarget.Search(East, ShardSearchMethod.Get, new Dictionary<int, string> { [0] = "Patient/a" }),
            ShardCountTarget.Search(West, ShardSearchMethod.Get, NoReplacements),
        ];

        await Should.ThrowAsync<ArgumentException>(
            () => CountFixture.Executor(_client, capabilities).ExecuteAsync(plan, targets, 2, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task BudgetSmallerThanTheFanOutIsRefusedBeforeAnyCall()
    {
        FetchBudgetExhaustedException exhausted = await Should.ThrowAsync<FetchBudgetExhaustedException>(
            () => Executor().ExecuteAsync(CountPlan, SearchEvery(_capabilities), 1, Token));

        exhausted.Message.ShouldContain("1");
        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProvenEmptyShardsCostNoBudget()
    {
        ShardCountTarget[] targets = [ShardCountTarget.Search(East, ShardSearchMethod.Get, NoReplacements), ShardCountTarget.ProvenEmpty(West)];

        int? total = await Executor().ExecuteAsync(CountPlan, targets, 1, Token);

        total.ShouldBe(1);
    }

    [Fact]
    public async Task NegativeBudgetIsRejected()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Executor().ExecuteAsync(CountPlan, SearchEvery(_capabilities), -1, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledCallerMakesNoCalls()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(
            () => Executor().ExecuteAsync(CountPlan, SearchEvery(_capabilities), 2, cancelled.Token));

        thrown.CancellationToken.ShouldBe(cancelled.Token);
        _client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NullArgumentsAreRejected()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => Executor().ExecuteAsync(null!, SearchEvery(_capabilities), 2, Token));
        await Should.ThrowAsync<ArgumentNullException>(() => Executor().ExecuteAsync(CountPlan, null!, 2, Token));
        _client.Calls.ShouldBeEmpty();
    }

    public static TheoryData<string> BadDialects => ["missing", "extra", "undefined"];

    [Theory]
    [MemberData(nameof(BadDialects))]
    public void DialectsMustCoverEveryRegisteredShardWithADefinedDialect(string bad)
    {
        Dictionary<ShardId, CountDialect> dialects = Dialects(_capabilities, CountDialect.CountZero);
        switch (bad)
        {
            case "missing":
                dialects.Remove(West);
                break;
            case "extra":
                dialects[ShardId.Parse("north")] = CountDialect.CountZero;
                break;
            default:
                dialects[West] = (CountDialect)99;
                break;
        }

        Should.Throw<ArgumentException>(() => new FederatedCountExecutor(_client, _capabilities, dialects, 4));
    }

    [Fact]
    public void ConstructorRejectsNullsAndANonPositiveParallelism()
    {
        Dictionary<ShardId, CountDialect> dialects = Dialects(_capabilities, CountDialect.CountZero);

        Should.Throw<ArgumentNullException>(() => new FederatedCountExecutor(null!, _capabilities, dialects, 4));
        Should.Throw<ArgumentNullException>(() => new FederatedCountExecutor(_client, null!, dialects, 4));
        Should.Throw<ArgumentNullException>(() => new FederatedCountExecutor(_client, _capabilities, null!, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => new FederatedCountExecutor(_client, _capabilities, dialects, 0));
    }

    [Fact]
    public void TargetFactoriesRejectBadArguments()
    {
        Should.Throw<ArgumentNullException>(() => ShardCountTarget.ProvenEmpty(null!));
        Should.Throw<ArgumentNullException>(() => ShardCountTarget.Search(null!, ShardSearchMethod.Get, NoReplacements));
        Should.Throw<ArgumentNullException>(() => ShardCountTarget.Search(East, ShardSearchMethod.Get, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => ShardCountTarget.Search(East, (ShardSearchMethod)99, NoReplacements));
    }

    [Fact]
    public async Task TargetKeepsItsOwnCopyOfTheReplacements()
    {
        SearchCertification certified = Certification(
            exactCountShapes: ["Observation?subject=,"],
            paths: [FullPath(ObservationSubjectPatient, forwardShapes: ["count:Observation?@Observation.subject:Patient"])]);
        SearchCapabilities capabilities = Capabilities(certified, certified);
        QueryPlan plan = Plan(capabilities, "Observation", "subject:Patient.name=x&_summary=count");
        var replacements = new Dictionary<int, string> { [0] = "Patient/a" };
        ShardCountTarget[] targets = [ShardCountTarget.Search(East, ShardSearchMethod.Get, replacements), ShardCountTarget.ProvenEmpty(West)];
        replacements[0] = "Patient/changed";

        await CountFixture.Executor(_client, capabilities).ExecuteAsync(plan, targets, 1, Token);

        _client.WireTo(East).ShouldBe("subject=Patient%2Fa");
    }

    private static FakeShardClient Answering()
    {
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);
        return client;
    }
}

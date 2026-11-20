using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>What a federated count sends to each shard, and that it sums every shard's exact total.</summary>
public sealed class FederatedCountRequestTests
{
    [Theory]
    [InlineData("name=x&_count=0")]
    [InlineData("name=x&_summary=count")]
    [InlineData("_count=0&name=x&_total=accurate")]
    public async Task CountOnlyPlanSendsOnlyTheBackendPredicatesAsACountOnlySearch(string query)
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", query);
        var client = new FakeShardClient();
        client.Answer(East, 3);
        client.Answer(West, 4);

        int? total = await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        total.ShouldBe(7);
        foreach (ShardId shard in new[] { East, West })
        {
            ShardSearchRequest sent = client.SentTo(shard);
            sent.PageSize.ShouldBe(0);
            sent.Type.ShouldBe(Type("Patient"));
            client.WireTo(shard).ShouldBe("name=x");
        }
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task KeepsTheTargetsSearchMethod(ShardSearchMethod method)
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0");
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);

        await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities, method), 2, Token);

        client.SentTo(East).Method.ShouldBe(method);
        client.SentTo(West).Method.ShouldBe(method);
    }

    [Fact]
    public async Task SummaryCountDialectAppendsSummaryCountAfterTheBackendPredicates()
    {
        SearchCapabilities capabilities = Certified("Patient?gender&name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_count=0&gender=male");
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);

        await Executor(client, capabilities, CountDialect.CountZeroWithSummaryCount).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        client.WireTo(East).ShouldBe("name=x&gender=male&_summary=count");
        client.SentTo(East).PageSize.ShouldBe(0);
    }

    [Fact]
    public async Task DialectIsChosenPerShard()
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_summary=count");
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);
        var dialects = new Dictionary<ShardId, CountDialect>
        {
            [East] = CountDialect.CountZero,
            [West] = CountDialect.CountZeroWithSummaryCount,
        };

        await new FederatedCountExecutor(client, capabilities, dialects, 4).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        client.WireTo(East).ShouldBe("name=x");
        client.WireTo(West).ShouldBe("name=x&_summary=count");
    }

    [Theory]
    [InlineData("_total=accurate")]
    [InlineData("_total=estimate")]
    public async Task PagedPlanAskingForATotalCountsEveryShardExactlyRegardlessOfThePageSize(string total)
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", $"name=x&_count=5&{total}&_sort=-_lastUpdated");
        var client = new FakeShardClient();
        client.Answer(East, 40);
        client.Answer(West, 2);

        int? counted = await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        counted.ShouldBe(42);
        client.SentTo(East).PageSize.ShouldBe(0);
        client.WireTo(East).ShouldBe("name=x&_sort=-_lastUpdated");
    }

    [Theory]
    [InlineData("name=x")]
    [InlineData("name=x&_count=5")]
    [InlineData("name=x&_total=none")]
    public async Task PlanWithoutACountReturnsNullWithoutCalls(string query)
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", query);
        var client = new FakeShardClient();

        int? total = await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 0, Token);

        total.ShouldBeNull();
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task IncludesAreNeverForwarded()
    {
        SearchCapabilities capabilities = Certified("Patient?name");
        QueryPlan plan = Plan(capabilities, "Patient", "name=x&_include=Patient:organization&_revinclude=Observation:subject&_total=accurate");
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);

        await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        client.WireTo(East).ShouldBe("name=x");
        client.WireTo(West).ShouldBe("name=x");
    }

    [Fact]
    public async Task KeepsRawMultiplicityEscapesAndOrder()
    {
        SearchCapabilities capabilities = Certified("Patient?gender&name&name=,");
        QueryPlan plan = Plan(capabilities, "Patient", "name=b&_count=0&gender=male&name=a%5C,c,d");
        var client = new FakeShardClient();
        client.Answer(East, 1);
        client.Answer(West, 1);

        await Executor(client, capabilities).ExecuteAsync(plan, SearchEvery(capabilities), 2, Token);

        client.WireTo(East).ShouldBe("name=b&gender=male&name=a%5C,c,d");
        client.WireTo(West).ShouldBe("name=b&gender=male&name=a%5C,c,d");
    }

    [Fact]
    public async Task AppliesEachShardsOwnResolvedChainReplacements()
    {
        SearchCertification certified = Certification(
            exactCountShapes: ["Observation?subject=,"],
            paths: [FullPath(ObservationSubjectPatient, forwardShapes: ["count:Observation?@Observation.subject:Patient"])]);
        SearchCapabilities capabilities = Capabilities(certified, certified);
        QueryPlan plan = Plan(capabilities, "Observation", "subject:Patient.name=x&_summary=count");
        var client = new FakeShardClient();
        client.Answer(East, 2);
        client.Answer(West, 5);
        ShardCountTarget[] targets =
        [
            ShardCountTarget.Search(East, ShardSearchMethod.Get, new Dictionary<int, string> { [0] = "Patient/a,https://west.example/fhir/Patient/b" }),
            ShardCountTarget.Search(West, ShardSearchMethod.Get, new Dictionary<int, string> { [0] = "Patient/c" }),
        ];

        int? total = await Executor(client, capabilities).ExecuteAsync(plan, targets, 2, Token);

        total.ShouldBe(7);
        client.WireTo(East).ShouldBe("subject=Patient%2Fa%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fb");
        client.WireTo(West).ShouldBe("subject=Patient%2Fc");
    }
}

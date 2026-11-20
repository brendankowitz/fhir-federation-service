using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Counts;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;
using static Ignixa.Federation.Core.UnitTests.Execution.ReverseChains.ReverseChainFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

public sealed class ReverseChainAdmissionTests
{
    private readonly ReverseChainFixture _f = new();

    [Fact]
    public async Task DistributedForwardRewriteIsRejectedAtAdmission()
    {
        QueryPlan plan = TestSearch.PlanOk("Observation", "subject:Patient.name=Sarah");
        var exception = await Should.ThrowAsync<ArgumentException>(() => _f.ResolveAsync(plan));
        exception.ParamName.ShouldBe("plan");
        _f.Client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "operation")]
    [InlineData(false, "operation")]
    [InlineData(false, "removed")]
    [InlineData(true, "swapped")]
    public async Task CurrentProofMustCoverThePlannedOperationAndExactlyItsShards(bool native, string change)
    {
        QueryPlan plan = _f.Plan(native: native);
        ReferencePath path = TestSearch.ObservationSubjectPatient;
        var wrongOperation = new ReferencePathCertification(path,
            native ? ReferencePathOperations.DistributedReverse : ReferencePathOperations.NativeReverse,
            ReferenceSearchForms.None, ReferenceSearchForms.None);
        var current = new ReverseChainFixture
        {
            Capabilities = change switch
            {
                "removed" => CountFixture.Certified(TestShards.Create(TestShards.East())),
                "swapped" => CountFixture.Certified(TestShards.Create(TestShards.East(),
                    TestShards.Shard("north", "n4rt", "https://north.example/fhir"))),
                _ => TestSearch.Capabilities(west: TestSearch.Certification(paths: [wrongOperation])),
            },
        };
        var exception = await Should.ThrowAsync<ReverseChainRefusedException>(() => current.ResolveAsync(plan));
        exception.Reason.ShouldBe(ReverseChainRefusal.NotCertified);
        exception.Path.ShouldBe(path);
        current.Client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "Observation")]
    [InlineData(false, "Patient")]
    [InlineData(false, "Observation")]
    [InlineData(true, "Patient")]
    public async Task BothEndsOfEveryReversePathMustBeAuthorized(bool native, string onlyAuthorized)
    {
        QueryPlan plan = _f.Plan(native: native);
        var exception = await Should.ThrowAsync<ReverseChainRefusedException>(() => _f.Resolver().ResolveAsync(
            plan, new HashSet<ResourceType> { ResourceType.Parse(onlyAuthorized) }, 100, TestContext.Current.CancellationToken));
        exception.Reason.ShouldBe(ReverseChainRefusal.UnauthorizedType);
        exception.Path.ShouldBe(plan.Occurrences[0].Hop!.Path);
        _f.Client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("client")]
    [InlineData("capabilities")]
    [InlineData("maxChainKeys")]
    [InlineData("maxChainSourceRows")]
    [InlineData("maxParallelCalls")]
    [InlineData("subqueryTimeout")]
    public void InvalidConfigurationIsRejectedAtConstruction(string parameter)
    {
        var exception = Should.Throw<ArgumentException>(() => new ReverseChainResolver(
            parameter == "client" ? null! : _f.Client,
            parameter == "capabilities" ? null! : _f.Capabilities,
            parameter == "maxChainKeys" ? 0 : 1,
            parameter == "maxChainSourceRows" ? 0 : 1,
            parameter == "maxParallelCalls" ? 0 : 1,
            parameter == "subqueryTimeout" ? TimeSpan.Zero : TimeSpan.FromSeconds(15)));
        exception.ParamName.ShouldBe(parameter);
    }

    [Theory]
    [InlineData(Has + "=1&name=x")]
    [InlineData("name=x")]
    public async Task NativePredicatesNeedNoSubquery(string query)
    {
        var result = await _f.ResolveAsync(_f.Plan(query, native: true), budget: 0);

        result.Calls.ShouldBe(0);
        _f.Client.Calls.ShouldBeEmpty();
        result.Targets.Select(t => t.Shard).ShouldBe([East, West]);
        foreach (var target in result.Targets)
        {
            target.Replacements.ShouldBeEmpty();
            QueryRewriter.Render(target.Parameters).ShouldBe(query);
            ShardSearchRequest request = target.Request(7);
            request.Shard.ShouldBe(target.Shard);
            request.Type.ShouldBe(Patient);
            request.PageSize.ShouldBe(7);
            request.Method.ShouldBe(ShardSearchMethod.PostSearch);
        }
    }
}

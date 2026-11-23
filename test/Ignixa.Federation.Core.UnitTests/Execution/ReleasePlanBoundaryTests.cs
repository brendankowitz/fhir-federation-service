using Ignixa.Abstractions;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Chains;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Search;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

public sealed class ReleasePlanBoundaryTests
{
    [Fact]
    public async Task ForwardChainResolver_RejectsPlanFromAnotherView()
    {
        ShardRegistry registry = ChainScenario.Registry();
        QueryPlan plan = R5Plan(registry);
        ForwardChainResolver resolver = ChainScenario.Resolver(new FakeShardClient(), registry);

        await Should.ThrowAsync<ArgumentException>(() =>
            resolver.ResolveAsync(plan, ChainScenario.AllReadable, 0, 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevIncludeExecutor_RejectsPlanFromAnotherView()
    {
        ShardRegistry registry = ChainScenario.Registry();
        QueryPlan plan = R5Plan(registry);
        var executor = new RevIncludePageExecutor(new FakeShardClient(), registry, TestSearch.R4Specification, 1, _ => true);

        await Should.ThrowAsync<ArgumentException>(() =>
            executor.ExecuteAsync(plan, [], ChainScenario.AllReadable, 0, 0, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(FhirVersion.R4, "4.0.1")]
    [InlineData(FhirVersion.R5, "5.0.0")]
    public async Task ForwardIncludeResolver_RejectsOccurrenceFromAnotherViewBeforeAnyCall(FhirVersion version, string shardVersion)
    {
        ShardRegistry registry = ChainScenario.Registry();
        IFederationSearchSpecification other = ReleaseCompositionTests.Compose(version);
        var capabilities = new SearchCapabilities(registry, registry.Shards.Select(s =>
            new ShardSearchCapability(s.Id, TestSearch.AllResources, TestSearch.Certification(), shardVersion)), other);
        QueryPlan plan = TestSearch.PlanOk("Observation", "_include=Observation:subject:Patient", TestSearch.Planner(capabilities));
        var client = new FakeShardClient();
        var resolver = new ForwardIncludeResolver(client, registry, 100, TestSearch.R4Specification);

        var invalidMatch = IncludeFixture.Resource(
            IncludeFixture.East, IncludeFixture.Observation, "o1", "1", "\"subject\":{\"reference\":123}");
        PlannedOccurrence[] mixedIncludes = [IncludeFixture.ObservationSubject, .. plan.Occurrences];

        await Should.ThrowAsync<ArgumentException>(() =>
            resolver.ResolveAsync([invalidMatch], mixedIncludes, ChainScenario.AllReadable, 1, TestContext.Current.CancellationToken));
        client.Calls.ShouldBeEmpty();
    }

    private static QueryPlan R5Plan(ShardRegistry registry)
    {
        IFederationSearchSpecification specification = ReleaseCompositionTests.Compose(FhirVersion.R5);
        var capabilities = new SearchCapabilities(registry, registry.Shards.Select(s =>
            new ShardSearchCapability(s.Id, TestSearch.AllResources, TestSearch.Certification(), "5.0.0")), specification);
        return TestSearch.PlanOk("Patient", "", new SearchPlanner(capabilities, new ChainResolutionOptions(), new LimitOptions()));
    }
}

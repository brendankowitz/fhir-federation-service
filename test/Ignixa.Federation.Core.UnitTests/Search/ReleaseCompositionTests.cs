using Ignixa.Abstractions;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Parsing;
using Ignixa.Specification.Extensions;
using Ignixa.Specification.ValueSets.Normative;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class ReleaseCompositionTests
{
    [Fact]
    public void SameAdvertisement_IsPublishedAndPlansOnlyInTheR5View()
    {
        ResourceSearchSupport advertised = new(Type("EncounterHistory"), [Param("_id", SearchParamType.Token)], [], []);
        SearchCapabilities r4 = View(Compose(FhirVersion.R4), "4.0.1", advertised);
        SearchCapabilities r5 = View(Compose(FhirVersion.R5), "5.0.0", advertised);

        r4.Specification.IsReferenceValue("EncounterHistory/h1").ShouldBeFalse();
        r5.Specification.IsReferenceValue("EncounterHistory/h1").ShouldBeTrue();
        r4.Resources.ShouldBeEmpty();
        r5.Resources.ShouldHaveSingleItem().Type.Value.ShouldBe("EncounterHistory");
        r5.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id"]);
        new SearchPlanner(r4, new ChainResolutionOptions(), new LimitOptions()).Plan(Type("EncounterHistory"), Query("")).Failure
            .ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedResourceType);
        new SearchPlanner(r5, new ChainResolutionOptions(), new LimitOptions()).Plan(Type("EncounterHistory"), Query("")).Plan.ShouldNotBeNull();
        new SearchPlanner(r5, new ChainResolutionOptions(), new LimitOptions()).Plan(Type("EncounterHistory"), Query("_id=h1")).Plan.ShouldNotBeNull();
    }

    [Fact]
    public async Task ConcurrentCompilation_KeepsEachOccurrenceTraceIsolated()
    {
        IFederationSearchSpecification specification = Compose(FhirVersion.R5);
        Task[] calls = [.. Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            string value = $"h{i}";
            var trace = specification.Compile(Type("EncounterHistory"), "_id", value, SearchFailureCode.UnsupportedParameter, out _);
            trace.ShouldNotBeNull().Value.ShouldBe(value);
        }))];

        await Task.WhenAll(calls);
    }

    [Fact]
    public void SpecificationUsesItsSchemaVersionAndImmutableAcceptedVersions()
    {
        IFhirSchemaProvider schema = FhirVersion.R4.GetSchemaProvider();
        var versions = new List<string> { "4.0.0", "4.0.1" };
        FederationSearchSpecification specification = Compose(FhirVersion.R4, schema, versions);
        versions.Add("5.0.0");

        specification.Version.ShouldBe(FhirVersion.R4);
        specification.PublishedVersion.ShouldBe(schema.FullVersion);
        specification.AcceptedShardVersions.ShouldBe(["4.0.0", "4.0.1"], ignoreOrder: true);
        specification.AcceptedShardVersions.Contains("4.0.1").ShouldBeTrue();
        specification.AcceptedShardVersions.Contains("5.0.0").ShouldBeFalse();
    }

    [Theory]
    [InlineData("4.0.0")]
    [InlineData("4.0.1")]
    public void SameReleaseShardVersion_IsAccepted(string version)
    {
        View(Compose(FhirVersion.R4), version, AllResources[0]).Resources.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("4.0.1 ")]
    [InlineData("4.0")]
    [InlineData("5.0.0")]
    public void DifferentOrMalformedShardVersion_IsRejected(string version)
    {
        Should.Throw<ArgumentException>(() => View(Compose(FhirVersion.R4), version, AllResources[0]));
    }

    [Fact]
    public void MixedShardReleases_AreRejectedEvenWhenAnotherShardMatches()
    {
        ShardRegistry registry = Registry();
        ShardSearchCapability[] shards =
        [
            new(registry.Shards[0].Id, [AllResources[0]], Certification(), "4.0.0"),
            new(registry.Shards[1].Id, [AllResources[0]], Certification(), "5.0.0"),
        ];

        Should.Throw<ArgumentException>(() => new SearchCapabilities(registry, shards, Compose(FhirVersion.R4)));
    }

    [Fact]
    public void BothR4PatchVersions_CanParticipateInOneView()
    {
        ShardRegistry registry = Registry();
        ShardSearchCapability[] shards =
        [
            new(registry.Shards[0].Id, [AllResources[0]], Certification(), "4.0.0"),
            new(registry.Shards[1].Id, [AllResources[0]], Certification(), "4.0.1"),
        ];

        new SearchCapabilities(registry, shards, Compose(FhirVersion.R4)).Resources.ShouldNotBeEmpty();
    }

    internal static IFederationSearchSpecification Compose(FhirVersion version)
    {
        IFhirSchemaProvider schema = version.GetSchemaProvider();
        return Compose(version, schema, version == FhirVersion.R4 ? ["4.0.0", "4.0.1"] : [schema.FullVersion]);
    }

    private static FederationSearchSpecification Compose(FhirVersion version, IFhirSchemaProvider schema, IEnumerable<string> versions)
    {
        var manager = new SearchParameterDefinitionManager(schema, NullLogger<SearchParameterDefinitionManager>.Instance);
        SearchOptionsBuilder Compiler()
        {
            var references = new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance);
            var parser = new ExpressionParser(() => manager, new SearchParameterExpressionParser(references, schema), schema);
            return new SearchOptionsBuilder(parser, manager);
        }

        return new FederationSearchSpecification(version, schema, manager, Compiler, versions);
    }

    private static SearchCapabilities View(IFederationSearchSpecification specification, string version, ResourceSearchSupport resource)
    {
        ShardRegistry registry = Registry();
        return new SearchCapabilities(registry, registry.Shards.Select(s =>
            new ShardSearchCapability(s.Id, [resource], Certification(), version)), specification);
    }
}

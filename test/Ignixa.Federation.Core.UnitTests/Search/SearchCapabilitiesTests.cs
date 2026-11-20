using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchCapabilitiesTests
{
    private static ShardSearchCapability Shard(string id, IEnumerable<ResourceSearchSupport>? resources = null) =>
        new(ShardId.Parse(id), resources ?? AllResources, Certification());

    [Fact]
    public void RejectsAShardWithNoCapability()
    {
        Should.Throw<ArgumentException>(() => new SearchCapabilities(Registry(), [Shard("east")]));
    }

    [Fact]
    public void RejectsACapabilityForAnUnregisteredShard()
    {
        Should.Throw<ArgumentException>(() => new SearchCapabilities(Registry(), [Shard("east"), Shard("west"), Shard("north")]));
    }

    [Fact]
    public void RejectsTwoCapabilitiesForOneShard()
    {
        Should.Throw<ArgumentException>(() => new SearchCapabilities(Registry(), [Shard("east"), Shard("west"), Shard("west")]));
    }

    [Fact]
    public void RejectsAShardThatAdvertisesNoResources()
    {
        Should.Throw<ArgumentException>(() => Shard("east", []));
    }

    [Fact]
    public void RejectsAShardThatAdvertisesOneResourceTypeTwice()
    {
        Should.Throw<ArgumentException>(() => Shard("east", [AllResources[0], AllResources[0]]));
    }

    [Fact]
    public void PublishesOnlyResourceTypesEveryShardSupports()
    {
        SearchCapabilities capabilities = Capabilities(westResources: AllResources.Where(r => r.Type.Value != "Practitioner"));

        capabilities.Resources.Select(r => r.Type.Value).ShouldBe(
            ["Observation", "Organization", "Patient", "Questionnaire", "QuestionnaireResponse"]);
    }

    [Fact]
    public void PublishesOnlyParametersEveryShardAdvertisesWithTheR4Definition()
    {
        ResourceSearchSupport westPatient = new(Type("Patient"), [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String)], [], []);

        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        ResourceSearchSupport patient = capabilities.Resources.Single(r => r.Type.Value == "Patient");
        patient.Parameters.Select(p => p.Code).ShouldBe(["_id", "name"]);
        patient.Parameters.Single(p => p.Code == "name").Definition.ShouldBe(new Uri("http://hl7.org/fhir/SearchParameter/Patient-name"));
    }

    [Fact]
    public void DropsAParameterWhoseAdvertisedTypeDisagreesWithR4()
    {
        ResourceSearchSupport westPatient = new(Type("Patient"), [Param("_id", SearchParamType.Token), Param("name", SearchParamType.Token)], [], []);

        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        capabilities.Resources.Single(r => r.Type.Value == "Patient").Parameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    // A shard can bind a code to its own definition; that must not be read as the R4 meaning.
    [Fact]
    public void DropsAParameterBoundToAnotherDefinition()
    {
        ResourceSearchSupport westPatient = new(
            Type("Patient"),
            [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String, "https://example.org/SearchParameter/name")],
            [],
            []);

        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        capabilities.Resources.Single(r => r.Type.Value == "Patient").Parameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    [Fact]
    public void DropsACodeAdvertisedTwiceWithConflictingMeanings()
    {
        ResourceSearchSupport westPatient = new(
            Type("Patient"),
            [Param("_id", SearchParamType.Token), Param("name", SearchParamType.String), Param("name", SearchParamType.Token)],
            [],
            []);

        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        capabilities.Resources.Single(r => r.Type.Value == "Patient").Parameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    [Fact]
    public void DropsAParameterUnknownToR4()
    {
        ResourceSearchSupport patient = new(Type("Patient"), [Param("_id", SearchParamType.Token), Param("shoe-size", SearchParamType.Number)], [], []);

        SearchCapabilities capabilities = Capabilities(westResources: [patient, .. AllResources.Skip(1)]);

        capabilities.Resources.Single(r => r.Type.Value == "Patient").Parameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    [Fact]
    public void PublishesOnlyIncludesEveryShardAdvertises()
    {
        ResourceSearchSupport westPatient = new(AllResources[0].Type, AllResources[0].Parameters, ["Patient:organization"], ["Observation:subject"]);

        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        ResourceSearchSupport patient = capabilities.Resources.Single(r => r.Type.Value == "Patient");
        patient.SearchIncludes.ShouldBe(["Patient:organization"]);
        patient.SearchRevIncludes.ShouldBe(["Observation:subject"]);
    }

    [Fact]
    public void DoesNotPublishWildcardOrIterateIncludesEvenWhenEveryShardAdvertisesThem()
    {
        ResourceSearchSupport patient = new(
            Type("Patient"),
            AllResources[0].Parameters,
            ["*", "Patient:*", "Patient:organization:iterate", "Patient:organization"],
            ["*", "Observation:*:Patient", "Observation:subject:iterate", "Observation:subject"]);
        ShardRegistry registry = Registry();
        var capabilities = new SearchCapabilities(registry, registry.Shards.Select(
            s => new ShardSearchCapability(s.Id, [patient], Certification())));

        ResourceSearchSupport published = capabilities.Resources.Single();
        published.SearchIncludes.ShouldBe(["Patient:organization"]);
        published.SearchRevIncludes.ShouldBe(["Observation:subject"]);
    }

    [Fact]
    public void SupportsLastUpdatedSortOnlyWhenEveryShardHasStableTies()
    {
        Capabilities().SupportsLastUpdatedSort.ShouldBeTrue();

        ShardRegistry mixed = TestShards.Create(TestShards.East(), TestShards.West().WithProfile("Ignixa"));
        Capabilities(registry: mixed).SupportsLastUpdatedSort.ShouldBeFalse();
    }

    [Fact]
    public void RejectsAnUnknownModifierSpelling()
    {
        Should.Throw<ArgumentException>(() => Certification(modifiers: new Dictionary<SearchParamType, IReadOnlyCollection<string>>
        {
            [SearchParamType.String] = ["exact:x"],
        }));
    }

    [Fact]
    public void RejectsTwoCertificationsForOnePath()
    {
        Should.Throw<ArgumentException>(() => Certification(paths: [FullPath(ObservationSubjectPatient), FullPath(ObservationSubjectPatient)]));
    }
}

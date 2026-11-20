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

    private static void ShouldPublishIncludes(SearchCapabilities capabilities, string type, string[] includes, string[] revIncludes)
    {
        ResourceSearchSupport published = capabilities.Resources.Single(r => r.Type.Value == type);
        published.SearchIncludes.ShouldBe(includes, type);
        published.SearchRevIncludes.ShouldBe(revIncludes, type);
    }

    /// <summary>Checks a bare predicate for each published parameter and single-occurrence admission for common include forms.</summary>
    private static void ShouldPublishExactlyWhatPlans(SearchCapabilities capabilities, IEnumerable<ResourceSearchSupport> advertised)
    {
        SearchPlanner planner = Planner(capabilities);
        foreach (ResourceSearchSupport published in capabilities.Resources)
        {
            foreach (SearchParameterSupport parameter in published.Parameters)
            {
                string value = parameter.Type switch
                {
                    SearchParamType.String => "Smith",
                    SearchParamType.Token => "female",
                    SearchParamType.Date => "1990-04-03",
                    SearchParamType.Number or SearchParamType.Quantity => "2.5",
                    SearchParamType.Uri => "https://example.org/form",
                    _ => throw new InvalidOperationException($"No bare predicate witness for {published.Type.Value}.{parameter.Code}."),
                };
                string query = $"{parameter.Code}={value}";
                QueryPlan plan = Plan(published.Type.Value, query, planner).Plan.ShouldNotBeNull($"{published.Type.Value}?{query}");
                plan.Occurrences.ShouldHaveSingleItem().Kind.ShouldBe(OccurrenceKind.Predicate);
            }
        }

        foreach (ResourceSearchSupport resource in advertised)
        {
            ResourceSearchSupport published = capabilities.Resources.Single(r => r.Type == resource.Type);
            foreach ((string key, IReadOnlyList<string> forms, IReadOnlyList<string> publishedForms) in new[]
            {
                ("_include", resource.SearchIncludes, published.SearchIncludes),
                ("_revinclude", resource.SearchRevIncludes, published.SearchRevIncludes),
            })
            {
                foreach (string form in forms)
                {
                    bool plans = Plan(resource.Type.Value, $"{key}={form}", planner).Plan is not null;
                    publishedForms.Contains(form).ShouldBe(plans, $"{resource.Type.Value}?{key}={form}");
                }
            }
        }
    }

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

    [Theory]
    [InlineData("Patient", "_type", SearchParamType.Token, "Patient")]
    [InlineData("Patient", "_text", SearchParamType.String, "Smith")]
    [InlineData("Patient", "_source", SearchParamType.Uri, "https://example.org/source")]
    [InlineData("Patient", "_has", SearchParamType.Special, "Observation")]
    [InlineData("Patient", "_filter", SearchParamType.Special, "name%20eq%20Smith")]
    [InlineData("Location", "near", SearchParamType.Special, "40|-75|5|km")]
    public void DoesNotPublishAnUnplannableBareParameterEvenWhenEveryShardAdvertisesIt(
        string type, string code, SearchParamType parameterType, string value)
    {
        ResourceSearchSupport resource = new(Type(type), [Param(code, parameterType), Param("_id", SearchParamType.Token)], [], []);
        var capabilities = new SearchCapabilities(Registry(), [Shard("east", [resource]), Shard("west", [resource])]);

        PlanFails(type, $"{code}={value}", Planner(capabilities));
        capabilities.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id"]);
        ShouldPublishExactlyWhatPlans(capabilities, [resource]);
    }

    [Fact]
    public void PublishesOrdinaryBarePredicatesButNotCompositeParametersWithoutAGeneralWitness()
    {
        ResourceSearchSupport[] resources =
        [
            new(Type("Observation"), [.. AllResources[1].Parameters, Param("code-value-quantity", SearchParamType.Composite)], [], []),
            new(Type("RiskAssessment"), [Param("probability", SearchParamType.Number)], [], []),
            new(Type("Questionnaire"), [Param("url", SearchParamType.Uri)], [], []),
        ];
        var capabilities = new SearchCapabilities(Registry(), [Shard("east", resources), Shard("west", resources)]);

        capabilities.Resources.Single(r => r.Type.Value == "Observation").Parameters.Select(p => p.Code)
            .ShouldBe(["_id", "code", "date", "value-quantity"]);
        capabilities.Resources.Single(r => r.Type.Value == "RiskAssessment").Parameters.Single().Code.ShouldBe("probability");
        capabilities.Resources.Single(r => r.Type.Value == "Questionnaire").Parameters.Single().Code.ShouldBe("url");
        ShouldPublishExactlyWhatPlans(capabilities, resources);
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
            s => new ShardSearchCapability(s.Id, [patient, .. AllResources.Skip(1)], Certification())));

        ResourceSearchSupport published = capabilities.Resources.Single(r => r.Type.Value == "Patient");
        ShouldPublishExactlyWhatPlans(capabilities, [patient]);
        published.SearchIncludes.ShouldBe(["Patient:organization"]);
        published.SearchRevIncludes.ShouldBe(["Observation:subject"]);
    }

    // A parameter entry cannot say "`:missing` and chains only", and reference values are refused, so references stay
    // internal: published through the includes and revincludes they serve.
    [Fact]
    public void DoesNotPublishReferenceParametersButKeepsTheirIncludes()
    {
        SearchCapabilities capabilities = Capabilities();
        ResourceSearchSupport patient = capabilities.Resources.Single(r => r.Type.Value == "Patient");

        patient.Parameters.Select(p => p.Code).ShouldBe(["_id", "_lastUpdated", "birthdate", "family", "gender", "name"]);
        patient.SearchIncludes.ShouldBe(["Patient:organization"]);
        patient.SearchRevIncludes.ShouldBe(["Observation:subject"]);
        SearchPlanner planner = Planner(capabilities);
        PlanOk("Patient", "organization:missing=true", planner).Occurrences.Single().Kind.ShouldBe(OccurrenceKind.Predicate);
        PlanOk("Patient", "organization.name=Example", planner).Occurrences.Single().Kind.ShouldBe(OccurrenceKind.ForwardChain);
        ShouldFail("Patient", "organization=Organization/1", SearchFailureCode.UnsupportedReferenceFilter, planner: planner);
    }

    // Metadata states what a request admits: a common include that does not plan alone is withheld, even when every shard
    // advertises it (a polymorphic target, an uncertified path, a canonical reference).
    [Fact]
    public void PublishesExactlyTheCommonIncludesThatPlanAlone()
    {
        SearchCapabilities capabilities = Capabilities();

        ShouldPublishExactlyWhatPlans(capabilities, AllResources);
        ShouldPublishIncludes(capabilities, "Patient", ["Patient:organization"], ["Observation:subject"]);
        ShouldPublishIncludes(capabilities, "Observation", ["Observation:subject:Patient"], []);
        ShouldPublishIncludes(capabilities, "Organization", [], ["Patient:organization"]);
        ShouldPublishIncludes(capabilities, "QuestionnaireResponse", [], []);
        ShouldPublishIncludes(capabilities, "Questionnaire", [], []);
    }

    // Coverage attested for another registry proves no revinclude search; extraction needs no coverage.
    [Fact]
    public void WithholdsARevIncludeWhoseRegistryCoverageIsStale()
    {
        ShardRegistry aliased = TestShards.Create(TestShards.East("https://east-legacy.example/fhir"), TestShards.West());
        SearchCapabilities capabilities = Capabilities(registry: aliased);

        ShouldPublishExactlyWhatPlans(capabilities, AllResources);
        ShouldPublishIncludes(capabilities, "Patient", ["Patient:organization"], []);
        ShouldPublishIncludes(capabilities, "Organization", [], []);
    }

    // An untyped polymorphic include is not published, yet still covers its certified typed form on request; the typed form
    // is not published because no shard advertised it.
    [Fact]
    public void KeepsTheTypedFallbackWithoutPublishingTheUntypedOrSynthesizingTheTypedInclude()
    {
        ResourceSearchSupport[] resources =
        [
            AllResources[0],
            new(Type("Observation"), AllResources[1].Parameters, ["Observation:subject"], []),
            .. AllResources.Skip(2),
        ];
        ShardRegistry registry = Registry();
        var capabilities = new SearchCapabilities(registry, registry.Shards.Select(s => new ShardSearchCapability(s.Id, resources, Certification())));
        SearchPlanner planner = Planner(capabilities);

        ShouldPublishExactlyWhatPlans(capabilities, resources);
        ShouldPublishIncludes(capabilities, "Observation", [], []);
        ShouldPublishIncludes(capabilities, "Patient", ["Patient:organization"], ["Observation:subject"]);
        PlanOk("Observation", "_include=Observation:subject:Patient", planner).Occurrences.ShouldHaveSingleItem().Hop!.Path.ShouldBe(ObservationSubjectPatient);
        PlanOk("Patient", "_revinclude=Observation:subject:Patient", planner).Occurrences.ShouldHaveSingleItem().Hop!.Path.ShouldBe(ObservationSubjectPatient);
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

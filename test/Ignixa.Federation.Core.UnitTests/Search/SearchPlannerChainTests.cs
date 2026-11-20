using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchPlannerChainTests
{
    private static SearchCertification WithPath(ReferencePathOperations operations)
    {
        bool searchesByReference = (operations & (ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch)) != 0;
        return Certification(paths:
        [
            new ReferencePathCertification(
                ObservationSubjectPatient,
                operations,
                searchesByReference ? ReferenceSearchForms.Relative : ReferenceSearchForms.None,
                searchesByReference ? ReferenceSearchForms.CanonicalAbsolute : ReferenceSearchForms.None,
                searchesByReference ? Registry().Version : null,
                operations.HasFlag(ReferencePathOperations.DistributedForward) ? [StandaloneShape(ObservationSubjectPatient)] : null,
                operations.HasFlag(ReferencePathOperations.RevIncludeSearch) ? StandaloneShape(ObservationSubjectPatient) : null),
            FullPath(PatientOrganization),
        ]);
    }

    [Fact]
    public void ReplacesAForwardChainWithAGeneratedTerminalByDefault()
    {
        SearchCertification certification = Certification(paths: [FullPath(ObservationSubjectPatient, forwardShapes: ["Observation?code=1&@Observation.subject:Patient"])]);
        PlannedOccurrence occurrence = PlanOk("Observation", "code=1&subject:Patient.name=smith", Planner(Capabilities(certification, certification))).Occurrences[1];

        occurrence.Kind.ShouldBe(OccurrenceKind.ForwardChain);
        occurrence.Action.ShouldBe(OccurrenceAction.Replace);
        ReferenceHop hop = occurrence.Hop.ShouldNotBeNull();
        hop.Path.ShouldBe(ObservationSubjectPatient);
        hop.Mode.ShouldBe(ChainResolutionMode.Distributed);
        hop.Terminal.ShouldBe(QueryRewriter.Encode("name", "smith"));
        hop.Definition.ShouldBe(new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"));
        hop.Expression.ShouldBe("Observation.subject");
        hop.Certifications.Keys.Select(k => k.Value).Order(StringComparer.Ordinal).ShouldBe(["east", "west"]);
        hop.Certifications[ShardId.Parse("east")].ForeignForms.ShouldBe(ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases);
    }

    [Fact]
    public void PreservesTheDecodedTerminalModifierAndValueExactly()
    {
        ReferenceHop hop = PlanOk("Observation", "subject:Patient.name:exact=a%5C,b%2Bc").Occurrences[0].Hop.ShouldNotBeNull();

        hop.Terminal.ShouldBe(QueryRewriter.Encode("name:exact", "a\\,b+c"));
    }

    [Fact]
    public void KeepsAForwardChainOnAPassthroughPath()
    {
        SearchPlanner planner = Planner(passthrough: "Observation.subject:Patient");

        PlannedOccurrence occurrence = PlanOk("Observation", "subject:Patient.name=smith", planner).Occurrences[0];

        occurrence.Action.ShouldBe(OccurrenceAction.Keep);
        occurrence.Hop!.Mode.ShouldBe(ChainResolutionMode.Passthrough);
        occurrence.Hop.Terminal.ShouldBeNull();
    }

    [Fact]
    public void PlansAnUntypedChainOverASingleTargetReference()
    {
        ReferenceHop hop = PlanOk("Patient", "organization.name=acme").Occurrences[0].Hop.ShouldNotBeNull();

        hop.Path.ShouldBe(PatientOrganization);
        hop.Expression.ShouldBe("Patient.managingOrganization");
    }

    [Fact]
    public void ReplacesAReverseChainWithATerminalForTheSourceType()
    {
        PlannedOccurrence occurrence = PlanOk("Patient", "_has:Observation:subject:code=1234").Occurrences[0];

        occurrence.Kind.ShouldBe(OccurrenceKind.ReverseChain);
        occurrence.Action.ShouldBe(OccurrenceAction.Replace);
        occurrence.Hop!.Path.ShouldBe(ObservationSubjectPatient);
        occurrence.Hop.Mode.ShouldBe(ChainResolutionMode.Distributed);
        occurrence.Hop.Terminal.ShouldBe(QueryRewriter.Encode("code", "1234"));
    }

    [Fact]
    public void KeepsAReverseChainOnAPassthroughPath()
    {
        PlannedOccurrence occurrence = PlanOk("Patient", "_has:Observation:subject:code=1234", Planner(passthrough: "Observation.subject:Patient")).Occurrences[0];

        occurrence.Action.ShouldBe(OccurrenceAction.Keep);
        occurrence.Hop!.Mode.ShouldBe(ChainResolutionMode.Passthrough);
    }

    [Theory]
    [InlineData("Observation", "subject:Patient.organization.name=x")]
    [InlineData("Patient", "_has:Observation:subject:_has:Observation:subject:code=1")]
    [InlineData("Observation", "subject:Patient._has:Observation:subject:code=1")]
    [InlineData("Observation", "subject:Patient.name:missing=true")]
    [InlineData("Observation", "subject:Patient.gender:not=male")]
    [InlineData("Observation", "subject:patient.name=x")]
    [InlineData("Observation", "performer:Organization.name=x")]
    [InlineData("QuestionnaireResponse", "questionnaire.name=x")]
    [InlineData("QuestionnaireResponse", "questionnaire:Questionnaire.name=x")]
    public void RejectsChainsOutsideTheCertifiedOneHopShape(string type, string query)
    {
        ShouldFail(type, query, SearchFailureCode.UnsupportedChain);
    }

    [Theory]
    [InlineData("Patient", "_has:Observation:subject=x")]
    [InlineData("Observation", "subject:Patient.name:not=x")]
    [InlineData("Observation", "subject.=x")]
    public void RejectsChainsWithoutAUsableTerminal(string type, string query)
    {
        PlanFails(type, query).IsNotSupported.ShouldBeTrue();
    }

    // R4 lets an untyped hop mean any target; compilation would pick one, so the federation refuses to guess.
    [Fact]
    public void RejectsAnUntypedChainOverAPolymorphicReference()
    {
        SearchFailure failure = PlanFails("Observation", "patient.name=x");

        failure.Code.ShouldBe(SearchFailureCode.ChainTargetAmbiguous);
        failure.IsNotSupported.ShouldBeFalse();
    }

    [Fact]
    public void RejectsAnEmptyChainTerminalValueAsInvalid()
    {
        ShouldFail("Observation", "subject:Patient.name=", SearchFailureCode.InvalidValue);
    }

    [Fact]
    public void RejectsAPassthroughChainWithoutNativeProof()
    {
        SearchPlanner planner = Planner(Capabilities(west: WithPath(ReferencePathOperations.DistributedForward)), "Observation.subject:Patient");

        ShouldFail("Observation", "subject:Patient.name=x", SearchFailureCode.UnsupportedChain, planner: planner);
    }

    [Fact]
    public void RejectsADistributedChainWithoutDistributedProof()
    {
        SearchPlanner planner = Planner(Capabilities(west: WithPath(ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse)));

        ShouldFail("Observation", "subject:Patient.name=x", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Patient", "_has:Observation:subject:code=1", SearchFailureCode.UnsupportedChain, planner: planner);
    }

    [Fact]
    public void RejectsAChainWhoseTerminalIsNotAdvertisedOnTheTarget()
    {
        ResourceSearchSupport westPatient = new(Type("Patient"), [Param("_id", SearchParamType.Token)], [], []);
        SearchPlanner planner = Planner(Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]));

        ShouldFail("Observation", "subject:Patient.name=x", SearchFailureCode.UnsupportedParameter, planner: planner);
    }

    [Fact]
    public void ADistributedForwardChainNeedsReferenceAlternativesProof()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(or: [SearchParamType.Token, SearchParamType.String])));

        ShouldFail("Observation", "code=1&subject:Patient.name=x", SearchFailureCode.UnsupportedAlternatives, 1, planner);
        PlanOk("Patient", "_has:Observation:subject:code=1", planner);
    }

    [Fact]
    public void ADistributedReverseChainNeedsIdAlternativesProof()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(or: [SearchParamType.Reference])));

        ShouldFail("Patient", "_has:Observation:subject:code=1", SearchFailureCode.UnsupportedAlternatives, planner: planner);
    }

    // A distributed reverse hop sends `_id=` alternatives to the searched type, so every shard must advertise its `_id`.
    [Fact]
    public void ADistributedReverseChainNeedsTheSearchedTypeIdAdvertised()
    {
        ResourceSearchSupport patient = AllResources[0];
        ResourceSearchSupport westPatient = new(
            patient.Type,
            [.. patient.Parameters.Where(p => p.Code != "_id")],
            patient.SearchIncludes,
            patient.SearchRevIncludes);
        SearchCapabilities capabilities = Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]);

        ShouldFail("Patient", "_has:Observation:subject:code=1", SearchFailureCode.UnsupportedParameter, planner: Planner(capabilities));
        PlanOk("Patient", "_has:Observation:subject:code=1", Planner(capabilities, "Observation.subject:Patient"))
            .Occurrences[0].Action.ShouldBe(OccurrenceAction.Keep);
        PlanOk("Observation", "subject:Patient.name=x", Planner(capabilities))
            .Occurrences[0].Action.ShouldBe(OccurrenceAction.Replace);
    }

    // Runtime evidence (Ignixa.Search 0.6.82): a `:text` terminal compiles one string across an unescaped comma.
    [Theory]
    [InlineData("Observation", "subject:Patient.gender:text=male,female")]
    [InlineData("Patient", "_has:Observation:subject:code:text=heart,rate")]
    public void RejectsTerminalAlternativesTheCompilerReadAsOneValue(string type, string query)
    {
        ShouldFail(type, query, SearchFailureCode.UnsupportedAlternatives);
    }

    [Fact]
    public void TwoDistributedChainsOnOneReferenceAreARepetitionOfThatReference()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(and: [SearchParamType.String, SearchParamType.Token])));

        ShouldFail("Observation", "subject:Patient.name=a&subject:Patient.gender=male", SearchFailureCode.UnsupportedRepetition, 1, planner);
        ShouldFail("Observation", "subject:missing=false&subject:Patient.name=a", SearchFailureCode.UnsupportedRepetition, 1, planner);
    }

    [Fact]
    public void PassthroughChainsRepeatOnlyWhenTheirForwardedKeysMatch()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(and: [])), "Observation.subject:Patient");

        PlanOk("Observation", "subject:Patient.name=a&subject:Patient.gender=male", planner);
    }

    [Fact]
    public void ExactCountForADistributedChainUsesTheRewrittenShape()
    {
        SearchCertification certified = Certification(
            exactCountShapes: ["Observation?subject=,"],
            paths: [FullPath(ObservationSubjectPatient, forwardShapes: ["count:Observation?@Observation.subject:Patient"])]);

        QueryPlan plan = PlanOk("Observation", "subject:Patient.name=x&_summary=count", Planner(Capabilities(certified, certified)));

        plan.CountOnly.ShouldBeTrue();
        plan.CountShape.ShouldBe("Observation?subject=,");
    }
}

using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchPlannerIncludeTests
{
    [Fact]
    public void RemovesAnIncludeAndExposesItsExtractionPath()
    {
        PlannedOccurrence occurrence = PlanOk("Patient", "name=x&_include=Patient:organization").Occurrences[1];

        occurrence.Kind.ShouldBe(OccurrenceKind.Include);
        occurrence.Action.ShouldBe(OccurrenceAction.Remove);
        ReferenceHop hop = occurrence.Hop.ShouldNotBeNull();
        hop.Path.ShouldBe(PatientOrganization);
        hop.Expression.ShouldBe("Patient.managingOrganization");
        hop.Mode.ShouldBeNull();
        hop.Terminal.ShouldBeNull();
    }

    [Theory]
    [InlineData("Observation", "_include=Observation:subject:Patient")]
    [InlineData("Observation", "_include=Observation:performer:Practitioner")]
    [InlineData("Patient", "_revinclude=Observation:subject")]
    [InlineData("Patient", "_revinclude=Observation:subject:Patient")]
    public void AcceptsAnAdvertisedCertifiedInclude(string type, string query)
    {
        PlannedOccurrence occurrence = PlanOk(type, query).Occurrences.ShouldHaveSingleItem();

        occurrence.Action.ShouldBe(OccurrenceAction.Remove);
        occurrence.Hop.ShouldNotBeNull();
    }

    [Fact]
    public void ARevIncludeIsAPathFromTheIncludedTypeToTheSearchedType()
    {
        PlannedOccurrence occurrence = PlanOk("Patient", "_revinclude=Observation:subject").Occurrences[0];

        occurrence.Kind.ShouldBe(OccurrenceKind.RevInclude);
        occurrence.Hop!.Path.ShouldBe(ObservationSubjectPatient);
    }

    [Theory]
    [InlineData("Patient", "_include=Patient")]
    [InlineData("Patient", "_include=Patient:organization:organization")]
    [InlineData("Patient", "_include=Patient:organization:Organization:x")]
    [InlineData("Patient", "_include=Observation:subject")]
    [InlineData("Patient", "_include=Patient:name")]
    [InlineData("Patient", "_include=Patient:organization:Practitioner")]
    [InlineData("Patient", "_revinclude=Observation:subject:Group")]
    [InlineData("Organization", "_revinclude=Observation:subject")]
    [InlineData("Patient", "_include=")]
    public void RejectsAMalformedOrMeaninglessIncludeAsInvalid(string type, string query)
    {
        ShouldFail(type, query, SearchFailureCode.InvalidValue);
    }

    [Theory]
    [InlineData("Patient", "_include=Patient:*")]
    [InlineData("Patient", "_include=*")]
    [InlineData("Patient", "_include:iterate=Patient:organization")]
    [InlineData("Patient", "_revinclude:iterate=Observation:subject")]
    [InlineData("Patient", "_include=Patient:link")]
    [InlineData("Patient", "_include=Patient:general-practitioner")]
    [InlineData("Observation", "_include=Observation:subject")]
    [InlineData("Patient", "_revinclude=Observation:performer")]
    [InlineData("QuestionnaireResponse", "_include=QuestionnaireResponse:questionnaire")]
    [InlineData("Questionnaire", "_revinclude=QuestionnaireResponse:questionnaire")]
    public void RejectsAnIncludeTheFederationCannotProve(string type, string query)
    {
        ShouldFail(type, query, SearchFailureCode.UnsupportedInclude);
    }

    [Fact]
    public void RejectsAnIncludeNotEveryShardAdvertises()
    {
        ResourceSearchSupport westPatient = new(AllResources[0].Type, AllResources[0].Parameters, [], AllResources[0].SearchRevIncludes);
        SearchPlanner planner = Planner(Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]));

        ShouldFail("Patient", "_include=Patient:organization", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    [Fact]
    public void RejectsAnIncludeWithoutExtractionOrRevIncludeSearchProof()
    {
        SearchCertification native = Certification(paths:
        [
            new ReferencePathCertification(PatientOrganization, ReferencePathOperations.NativeForward, ReferenceSearchForms.None, ReferenceSearchForms.None),
            new ReferencePathCertification(ObservationSubjectPatient, ReferencePathOperations.NativeReverse, ReferenceSearchForms.None, ReferenceSearchForms.None),
        ]);
        SearchPlanner planner = Planner(Capabilities(west: native));

        ShouldFail("Patient", "_include=Patient:organization", SearchFailureCode.UnsupportedInclude, planner: planner);
        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    [Fact]
    public void IncludesNeedAlternativesProofForTheirFollowUpSearch()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(or: [])));

        ShouldFail("Patient", "_include=Patient:organization", SearchFailureCode.UnsupportedInclude, planner: planner);
        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    [Fact]
    public void IncludesDoNotChangeTheCountShape()
    {
        SearchCertification certified = Certification(exactCountShapes: ["Patient?name"]);

        QueryPlan plan = PlanOk("Patient", "name=x&_include=Patient:organization&_total=accurate", Planner(Capabilities(certified, certified)));

        plan.CountShape.ShouldBe("Patient?name");
        plan.IncludeTotal.ShouldBeTrue();
    }

    [Fact]
    public void ARevIncludeNeedsItsReferenceAdvertisedOnTheIncludedType()
    {
        ResourceSearchSupport westObservation = new(Type("Observation"), [Param("_id", SearchParamType.Token), Param("code", SearchParamType.Token)], [], []);
        SearchPlanner planner = Planner(Capabilities(westResources: [AllResources[0], westObservation, .. AllResources.Skip(2)]));

        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }
}

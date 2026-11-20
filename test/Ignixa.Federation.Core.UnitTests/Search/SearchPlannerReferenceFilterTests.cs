using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

/// <summary>
/// A caller's reference value names a resource by identity, which one shard cannot match exactly for the federation; it is
/// refused, as unsupported when well formed and as invalid otherwise. Hops on references are unaffected.
/// </summary>
public sealed class SearchPlannerReferenceFilterTests
{
    // Runtime evidence (Ignixa.Search 0.6.82): every one compiles to a reference equality predicate.
    [Theory]
    [InlineData("Patient", "organization=Organization/1")]
    [InlineData("Patient", "organization=1")]
    [InlineData("Patient", "organization:Organization=1")]
    [InlineData("Patient", "organization=Organization/1/_history/2")]
    [InlineData("Patient", "organization=https://east.example/fhir/Organization/1")]
    [InlineData("Patient", "organization=https://elsewhere.example/Organization/1")]
    [InlineData("Patient", "organization=http://elsewhere.example/Organization/1|2")]
    [InlineData("Patient", "organization=urn:uuid:0c3151bd-1cbf-4d64-b04d-cd9187a4c6e0")]
    [InlineData("Patient", "organization=Organization/1,Organization/2")]
    [InlineData("Patient", "organization=https://elsewhere.example/a%5C,b")]
    [InlineData("Observation", "subject:Patient.organization=Organization/1")]
    [InlineData("Observation", "subject:Patient.general-practitioner:Practitioner=1")]
    [InlineData("Patient", "_has:Observation:subject:performer=Practitioner/1")]
    public void RefusesAWellFormedReferenceValueAsUnsupported(string type, string query)
    {
        SearchFailure failure = ShouldFail(type, "_count=5&" + query, SearchFailureCode.UnsupportedReferenceFilter, 1);

        failure.IsNotSupported.ShouldBeTrue();
        failure.Message.ShouldBe("Search parameter 1 is not supported: searching by a reference value is not supported.");
    }

    [Fact]
    public void RefusesAReferenceValueOnAPassthroughChainTerminal()
    {
        SearchPlanner planner = Planner(Capabilities(), "Observation.subject:Patient");

        ShouldFail("Observation", "subject:Patient.organization=Organization/1", SearchFailureCode.UnsupportedReferenceFilter, planner: planner);
        ShouldFail("Patient", "_has:Observation:subject:performer=Practitioner/1", SearchFailureCode.UnsupportedReferenceFilter, planner: planner);
    }

    // Runtime evidence (Ignixa.Search 0.6.82): the compiler accepts each of these as a reference equality predicate.
    [Theory]
    [InlineData("Patient", "organization", "/")]
    [InlineData("Patient", "organization", "Organization/")]
    [InlineData("Patient", "organization", "http://")]
    [InlineData("Patient", "organization", "http:elsewhere.example")]
    [InlineData("Patient", "organization", "Foo/1")]
    [InlineData("Patient", "organization", "a%20b")]
    [InlineData("Patient", "organization", "Organization/1,/")]
    [InlineData("Patient", "organization", "https://elsewhere.example/a%5C%5Cb")]
    [InlineData("Patient", "organization:Organization", "/")]
    [InlineData("Observation", "subject:Patient.organization", "/")]
    [InlineData("Patient", "_has:Observation:subject:performer", "http://")]
    public void RefusesAMalformedReferenceValueAsInvalid(string type, string key, string value)
    {
        SearchFailure failure = ShouldFail(type, $"_count=5&{key}={value}", SearchFailureCode.InvalidValue, 1);

        failure.IsNotSupported.ShouldBeFalse();
        failure.Message.ShouldBe("Search parameter 1 is not valid: its value is not valid for the parameter.");
    }

    [Fact]
    public void KeepsMissingOnAReferenceWhenEveryShardCertifiesIt()
    {
        PlanOk("Patient", "organization:missing=true").Occurrences.ShouldHaveSingleItem().Kind.ShouldBe(OccurrenceKind.Predicate);

        SearchPlanner planner = Planner(Capabilities(west: Certification(modifiers: new Dictionary<SearchParamType, IReadOnlyCollection<string>>
        {
            [SearchParamType.Reference] = [SearchCertification.TypeModifier, "identifier"],
        })));
        ShouldFail("Patient", "organization:missing=true", SearchFailureCode.UnsupportedModifier, planner: planner);
    }

    // Runtime evidence (Ignixa.Search 0.6.82): `:identifier` on a reference is ignored, so even certified it is not planned.
    [Theory]
    [InlineData("Patient", "organization:identifier=http://sys|v")]
    [InlineData("Observation", "subject:Patient.organization:identifier=http://sys|v")]
    [InlineData("Patient", "_has:Observation:subject:performer:identifier=http://sys|v")]
    public void RefusesIdentifierOnAReferenceTheCompilerDoesNotBind(string type, string query)
    {
        SearchFailure failure = PlanFails(type, query);

        failure.IsNotSupported.ShouldBeTrue();
        failure.Code.ShouldNotBe(SearchFailureCode.UnsupportedReferenceFilter);
    }

    [Fact]
    public void KeepsHopsIncludesAndRevIncludesOverReferences()
    {
        QueryPlan forward = PlanOk("Observation", "subject:Patient.name=x&_include=Observation:subject:Patient");
        QueryPlan reverse = PlanOk("Patient", "_has:Observation:subject:code=1&_revinclude=Observation:subject");
        QueryPlan byId = PlanOk("Patient", "organization:Organization._id=1");

        forward.Occurrences.Select(o => o.Kind).ShouldBe([OccurrenceKind.ForwardChain, OccurrenceKind.Include]);
        reverse.Occurrences.Select(o => o.Kind).ShouldBe([OccurrenceKind.ReverseChain, OccurrenceKind.RevInclude]);
        byId.Occurrences.ShouldHaveSingleItem().Hop.ShouldNotBeNull().Path.ShouldBe(PatientOrganization);
    }
}

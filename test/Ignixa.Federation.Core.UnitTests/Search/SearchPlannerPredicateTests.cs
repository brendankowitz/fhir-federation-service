using Ignixa.Federation.Core.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchPlannerPredicateTests
{
    [Theory]
    [InlineData("name=smith")]
    [InlineData("name:exact=Smith")]
    [InlineData("name:contains=mi")]
    [InlineData("gender:not=male")]
    [InlineData("birthdate=ge2020-01-01")]
    [InlineData("_id=1")]
    [InlineData("_lastUpdated=gt2020")]
    [InlineData("name:missing=true")]
    [InlineData("organization=Organization/1")]
    [InlineData("organization:Organization=1")]
    public void KeepsACompiledAdvertisedPredicate(string query)
    {
        PlannedOccurrence occurrence = PlanOk("Patient", query).Occurrences.ShouldHaveSingleItem();

        occurrence.Kind.ShouldBe(OccurrenceKind.Predicate);
        occurrence.Action.ShouldBe(OccurrenceAction.Keep);
        occurrence.Hop.ShouldBeNull();
    }

    // Runtime evidence for the published boundary: an escaped comma is one value, %2B is a literal plus.
    [Theory]
    [InlineData("name=a%5C,b")]
    [InlineData("name=a%2Bb")]
    [InlineData("name=a%5C%5C")]
    public void KeepsEscapedAndPlusValuesAsOneAlternative(string query)
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(or: [SearchParamType.Token])));

        PlanOk("Patient", query, planner).Occurrences.ShouldHaveSingleItem().Action.ShouldBe(OccurrenceAction.Keep);
    }

    [Theory]
    [InlineData("Patient", "name", "")]
    [InlineData("Patient", "name", "a,,b")]
    [InlineData("Patient", "name", ",")]
    [InlineData("Patient", "name", "a%5C")]
    [InlineData("Patient", "birthdate", "notadate")]
    [InlineData("Patient", "_lastUpdated", "yesterday")]
    [InlineData("Observation", "value-quantity", "abc")]
    [InlineData("Observation", "code", "a|b|c")]
    public void RejectsABadValueAsInvalid(string type, string key, string value)
    {
        SearchFailure failure = ShouldFail(type, $"_count=5&{key}={value}", SearchFailureCode.InvalidValue, 1);

        if (!string.IsNullOrEmpty(value))
        {
            failure.Message.ShouldNotContain(value);
        }
    }

    [Theory]
    [InlineData("shoe-size=9")]
    [InlineData("NAME=x")]
    [InlineData("_list=1")]
    [InlineData("_text=x")]
    [InlineData("_content=x")]
    [InlineData("_has=x")]
    [InlineData("name:foo=x")]
    [InlineData("birthdate:exact=2020")]
    [InlineData("name:missing=perhaps")]
    public void RejectsUnknownOrIgnoredParametersAsNotSupported(string query)
    {
        SearchFailure failure = PlanFails("Patient", query);

        failure.IsNotSupported.ShouldBeTrue();
        failure.Ordinal.ShouldBe(0);
    }

    [Fact]
    public void RejectsAnR4ParameterNotEveryShardAdvertises()
    {
        ResourceSearchSupport westPatient = new(Type("Patient"), [Param("_id", SearchParamType.Token)], [], []);
        SearchPlanner planner = Planner(Capabilities(westResources: [westPatient, .. AllResources.Skip(1)]));

        ShouldFail("Patient", "name=x", SearchFailureCode.UnsupportedParameter, planner: planner);
    }

    [Fact]
    public void RejectsAModifierNotCertifiedForTheParameterType()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(modifiers: new Dictionary<SearchParamType, IReadOnlyCollection<string>>
        {
            [SearchParamType.String] = ["contains"],
        })));

        PlanOk("Patient", "name:contains=x", planner);
        ShouldFail("Patient", "name:exact=x", SearchFailureCode.UnsupportedModifier, planner: planner);
        ShouldFail("Patient", "organization:Organization=1", SearchFailureCode.UnsupportedModifier, planner: planner);
    }

    [Fact]
    public void RejectsOrAlternativesWithoutProofForTheParameterType()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(or: [SearchParamType.Token])));

        PlanOk("Patient", "gender=male,female", planner);
        ShouldFail("Patient", "_count=1&name=a,b", SearchFailureCode.UnsupportedAlternatives, 1, planner);
    }

    [Fact]
    public void RejectsRepetitionWithoutProofForTheParameterType()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(and: [SearchParamType.Token])));

        PlanOk("Patient", "gender=male&gender:not=female&name=x", planner);
        ShouldFail("Patient", "name=a&gender=male&name=b", SearchFailureCode.UnsupportedRepetition, 2, planner);
    }

    // Different modifiers are still one parameter repeated: the backend ANDs them on the same code.
    [Fact]
    public void CountsModifiedOccurrencesOfOneCodeAsRepetition()
    {
        SearchPlanner planner = Planner(Capabilities(west: Certification(and: [])));

        ShouldFail("Patient", "name=a&name:exact=b", SearchFailureCode.UnsupportedRepetition, 1, planner);
    }

    [Fact]
    public void ExactCountMatchesTheNormalizedShapeOnEveryShard()
    {
        SearchCertification certified = Certification(exactCountShapes: ["Patient?gender=,&name:exact"]);
        SearchPlanner planner = Planner(Capabilities(certified, certified));

        QueryPlan plan = PlanOk("Patient", "name:exact=Smith&_total=accurate&gender=male,female&_count=3", planner);

        plan.IncludeTotal.ShouldBeTrue();
        plan.CountShape.ShouldBe("Patient?gender=,&name:exact");
        ShouldFail("Patient", "name:exact=Smith&_total=accurate&gender=male", SearchFailureCode.ExactCountNotCertified, 1, planner);
    }

    [Fact]
    public void ReportsTheFirstFailingOccurrence()
    {
        ShouldFail("Patient", "name=x&shoe-size=9&birthdate=bad", SearchFailureCode.UnsupportedParameter, 1);
    }

    [Fact]
    public void FailureTextNeverEchoesTheQuery()
    {
        SearchFailure failure = PlanFails("Patient", "secret-code=4111111111111111");

        failure.Message.ShouldNotContain("secret");
        failure.Message.ShouldNotContain("4111");
    }
}

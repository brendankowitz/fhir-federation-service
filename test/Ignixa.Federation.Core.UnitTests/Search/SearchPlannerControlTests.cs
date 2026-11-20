using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchPlannerControlTests
{
    [Fact]
    public void UsesTheDefaultPageSizeWhenNoCountIsGiven()
    {
        QueryPlan plan = PlanOk("Patient", "name=x");

        plan.PageSize.ShouldBe(new LimitOptions().DefaultPageSize);
        plan.CountOnly.ShouldBeFalse();
        plan.IncludeTotal.ShouldBeFalse();
        plan.Sort.ShouldBeNull();
    }

    [Theory]
    [InlineData("_count=7", 7)]
    [InlineData("_count=007", 7)]
    [InlineData("_count=1000", 100)]
    [InlineData("_count=99999999999999999999", 100)]
    public void BoundsTheRequestedPageSizeByTheMaximum(string query, int expected)
    {
        PlanOk("Patient", query).PageSize.ShouldBe(expected);
    }

    [Fact]
    public void BoundsTheDefaultPageSizeByTheMaximum()
    {
        var limits = new LimitOptions { DefaultPageSize = 50, MaxPageSize = 10 };
        var planner = new SearchPlanner(Capabilities(), new ChainResolutionOptions(), limits);

        PlanOk("Patient", "name=x", planner).PageSize.ShouldBe(10);
    }

    [Fact]
    public void RejectsLimitsWithoutAPositivePageSize()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new SearchPlanner(Capabilities(), new ChainResolutionOptions(), new LimitOptions { MaxPageSize = 0 }));
    }

    [Theory]
    [InlineData("_count=-1")]
    [InlineData("_count=1.5")]
    [InlineData("_count=%2B5")]
    [InlineData("_count=")]
    [InlineData("_count=%EF%BC%95")]
    public void RejectsACountThatIsNotDigits(string query)
    {
        ShouldFail("Patient", query, SearchFailureCode.InvalidValue);
    }

    [Theory]
    [InlineData("_count=0&_total=accurate")]
    [InlineData("_summary=count&_total=accurate")]
    public void PlansCountOnlyWithoutAPage(string query)
    {
        SearchCertification certified = Certification(exactCountShapes: ["Patient?"]);
        SearchPlanner planner = Planner(Capabilities(certified, certified));

        QueryPlan plan = PlanOk("Patient", query, planner);

        plan.CountOnly.ShouldBeTrue();
        plan.PageSize.ShouldBeNull();
        plan.Occurrences.ShouldAllBe(o => o.Kind == OccurrenceKind.Control && o.Action == OccurrenceAction.Remove);
    }

    [Theory]
    [InlineData("_summary=true")]
    [InlineData("_summary=text")]
    [InlineData("_summary=data")]
    [InlineData("_summary=false")]
    [InlineData("_elements=id")]
    public void RejectsSummaryAndElementsShapesAsNotSupported(string query)
    {
        SearchFailure failure = ShouldFail("Patient", query, SearchFailureCode.UnsupportedParameter);
        failure.IsNotSupported.ShouldBeTrue();
    }

    [Theory]
    [InlineData("_summary=yes")]
    [InlineData("_summary=Count")]
    [InlineData("_total=all")]
    [InlineData("_total=")]
    public void RejectsUnknownSummaryAndTotalValuesAsInvalid(string query)
    {
        SearchFailure failure = ShouldFail("Patient", query, SearchFailureCode.InvalidValue);
        failure.IsNotSupported.ShouldBeFalse();
    }

    [Fact]
    public void TotalNoneAsksForNothing()
    {
        PlanOk("Patient", "_total=none").IncludeTotal.ShouldBeFalse();
    }

    [Theory]
    [InlineData("_total=accurate")]
    [InlineData("_total=estimate")]
    public void TotalNeedsExactCountProofEvenForAnEstimate(string query)
    {
        ShouldFail("Patient", "name=x&" + query, SearchFailureCode.ExactCountNotCertified, 1);
    }

    [Fact]
    public void TotalNeedsExactCountProofOnEveryShard()
    {
        // East is certified for nothing; only west proves the shape.
        SearchPlanner planner = Planner(Capabilities(west: Certification(exactCountShapes: ["Patient?name"])));

        ShouldFail("Patient", "name=x&_total=accurate", SearchFailureCode.ExactCountNotCertified, 1, planner);
    }

    [Fact]
    public void ControlNamesAreExactCase()
    {
        ShouldFail("Patient", "_COUNT=5", SearchFailureCode.UnsupportedParameter);
    }

    [Theory]
    [InlineData("_count=5&_count=6")]
    [InlineData("_summary=count&_summary=count")]
    [InlineData("_total=none&_total=none")]
    [InlineData("_sort=_lastUpdated&_sort=_lastUpdated")]
    [InlineData("_format=json&_format=json")]
    public void RejectsARepeatedSingletonControlAtTheRepeat(string query)
    {
        ShouldFail("Patient", query, SearchFailureCode.DuplicateControl, 1);
    }

    [Theory]
    [InlineData("_format=json")]
    [InlineData("_format=application/json")]
    [InlineData("_format=application/fhir%2Bjson")]
    [InlineData("_format=application/fhir+json")]
    public void AcceptsAndRemovesJsonFormats(string query)
    {
        PlannedOccurrence occurrence = PlanOk("Patient", query).Occurrences.ShouldHaveSingleItem();

        occurrence.Kind.ShouldBe(OccurrenceKind.Control);
        occurrence.Action.ShouldBe(OccurrenceAction.Remove);
    }

    [Theory]
    [InlineData("_format=xml")]
    [InlineData("_format=application/fhir%2Bxml")]
    [InlineData("_format=text/turtle")]
    public void RejectsOtherFormatsAsAFormatIssue(string query)
    {
        ShouldFail("Patient", query, SearchFailureCode.UnsupportedFormat);
    }

    [Theory]
    [InlineData("_sort=_lastUpdated", LastUpdatedSort.Ascending)]
    [InlineData("_sort=-_lastUpdated", LastUpdatedSort.Descending)]
    public void KeepsALastUpdatedSortWithItsDirection(string query, LastUpdatedSort expected)
    {
        QueryPlan plan = PlanOk("Patient", query);

        plan.Sort.ShouldBe(expected);
        PlannedOccurrence occurrence = plan.Occurrences.ShouldHaveSingleItem();
        occurrence.Kind.ShouldBe(OccurrenceKind.Sort);
        occurrence.Action.ShouldBe(OccurrenceAction.Keep);
    }

    [Theory]
    [InlineData("_sort=name")]
    [InlineData("_sort=_lastUpdated,name")]
    [InlineData("_sort=_lastupdated")]
    public void RejectsAnyOtherSort(string query)
    {
        ShouldFail("Patient", query, SearchFailureCode.UnsupportedSort);
    }

    [Fact]
    public void RejectsALastUpdatedSortWhenAShardLacksStableTies()
    {
        ShardRegistry mixed = TestShards.Create(TestShards.East(), TestShards.West().WithProfile("Ignixa"));

        ShouldFail("Patient", "_sort=_lastUpdated", SearchFailureCode.UnsupportedSort, planner: Planner(Capabilities(registry: mixed)));
    }

    [Theory]
    [InlineData("_contained=true")]
    [InlineData("_containedType=contained")]
    [InlineData("_score=true")]
    [InlineData("_pretty=true")]
    [InlineData("_maxresults=5")]
    [InlineData("_type=Patient")]
    [InlineData("_count:x=5")]
    public void RejectsOtherControlsAsNotSupported(string query)
    {
        ShouldFail("Patient", query, SearchFailureCode.UnsupportedParameter);
    }

    [Fact]
    public void PlansASoleContinuationTokenAsItsOwnPlanWithoutDecodingIt()
    {
        SearchPlanResult result = Plan("Patient", "ct=abc%2Fdef");

        result.Plan.ShouldBeNull();
        result.Failure.ShouldBeNull();
        ContinuationPlan continuation = result.Continuation.ShouldNotBeNull();
        continuation.Token.ShouldBe("abc%2Fdef");
        continuation.Occurrence.Ordinal.ShouldBe(0);
    }

    [Fact]
    public void RejectsAContinuationTokenWithOtherParameters()
    {
        ShouldFail("Patient", "name=x&ct=abc", SearchFailureCode.ContinuationNotAlone, 1);
    }

    [Fact]
    public void RejectsAnEmptyContinuationToken()
    {
        ShouldFail("Patient", "ct=", SearchFailureCode.InvalidValue);
    }

    [Fact]
    public void RejectsAResourceTypeTheFederationCannotSearch()
    {
        SearchFailure failure = ShouldFail("Encounter", "_id=1", SearchFailureCode.UnsupportedResourceType, SearchFailure.NoOrdinal);

        failure.IsNotSupported.ShouldBeTrue();
        failure.Message.ShouldNotContain("Encounter");
    }

    [Fact]
    public void KeepsTheOriginalQueryForBackendTemplates()
    {
        SearchQuery query = Query("name=%41b&_count=5");

        QueryPlan plan = Planner().Plan(Type("Patient"), query).Plan.ShouldNotBeNull();

        plan.Query.ShouldBeSameAs(query);
        plan.Occurrences.Select(o => o.Occurrence).ShouldBe(query.Occurrences);
    }
}

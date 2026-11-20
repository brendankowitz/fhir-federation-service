using Ignixa.Federation.Core.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class SearchFailurePlannerCodeTests
{
    [Fact]
    public void SaysNotSupportedRatherThanNotValidForUnsupportedReasons()
    {
        PlanFails("Patient", "_count=1&shoe-size=9").Message.ShouldStartWith("Search parameter 1 is not supported:");
        PlanFails("Patient", "_count=1&name=").Message.ShouldStartWith("Search parameter 1 is not valid:");
        PlanFails("Encounter", "_id=1").Message.ShouldStartWith("The search is not supported:");
    }

    [Fact]
    public void WireFailuresAreInvalidNotUnsupported()
    {
        SearchQuery.Parse("a=%zz").Failure.ShouldNotBeNull().IsNotSupported.ShouldBeFalse();
    }
}

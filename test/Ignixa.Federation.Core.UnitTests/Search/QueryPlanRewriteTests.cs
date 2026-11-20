using Ignixa.Federation.Core.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

public sealed class QueryPlanRewriteTests
{
    [Fact]
    public void RequiresEveryDistributedOccurrenceBeforeBuildingBackendParameters()
    {
        SearchCertification certification = Certification(paths:
        [
            FullPath(ObservationSubjectPatient, forwardShapes: ["Observation?@Observation.subject:Patient&code=A&@Observation.subject:Patient"]),
        ]);
        QueryPlan plan = PlanOk(
            "Observation",
            "subject:Patient.name=Alice&code=%41&_count=10&subject:Patient.name=Bob",
            Planner(Capabilities(certification, certification)));

        Should.Throw<ArgumentException>(() => plan.BuildBackendParameters(new Dictionary<int, string> { [0] = "Patient/a" }));
        Should.Throw<ArgumentException>(() => plan.BuildBackendParameters(new Dictionary<int, string>
        {
            [0] = "Patient/a",
            [1] = "must-not-replace-code",
            [3] = "Patient/b",
        }));
        Should.Throw<ArgumentException>(() => plan.BuildBackendParameters(new Dictionary<int, string> { [0] = "Patient/a", [3] = "" }));

        QueryRewriter.Render(plan.BuildBackendParameters(new Dictionary<int, string>
        {
            [0] = "Patient/a,https://west.example/fhir/Patient/b",
            [3] = "Patient/c",
        })).ShouldBe("subject=Patient%2Fa%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fb&code=%41&subject=Patient%2Fc");
    }

    [Fact]
    public void DistributedReverseChainUsesIdAsItsReplacementKey()
    {
        QueryPlan plan = PlanOk("Patient", "_has:Observation:subject:code=123");

        plan.Occurrences.Single().ReplacementKey.ShouldBe("_id");
        QueryRewriter.Render(plan.BuildBackendParameters(new Dictionary<int, string> { [0] = "a,b" })).ShouldBe("_id=a%2Cb");
    }
}

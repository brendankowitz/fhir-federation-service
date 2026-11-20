using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Search;

/// <summary>
/// A reference-search rewrite (distributed forward chain, revinclude) is admitted only when every shard attests that its
/// emitted forms cover the stored forms of the exact current registry, its link base aliases included.
/// </summary>
public sealed class SearchPlannerReferenceCoverageTests
{
    private static ShardRegistry Aliased() => TestShards.Create(
        TestShards.East("https://east-legacy.example/fhir"),
        TestShards.West("https://west-legacy.example/fhir"));

    private static ReferencePathCertification CanonicalOnly(ReferencePath path, ReferencePathOperations operations, string? coveredRegistryVersion) => new(
        path,
        operations,
        ReferenceSearchForms.CanonicalAbsolute,
        ReferenceSearchForms.CanonicalAbsolute,
        coveredRegistryVersion,
        operations.HasFlag(ReferencePathOperations.DistributedForward) ? [StandaloneShape(path)] : null,
        operations.HasFlag(ReferencePathOperations.RevIncludeSearch) ? StandaloneShape(path) : null);

    private static SearchPlanner Planner(ShardRegistry registry, ReferencePathOperations operations, string? coveredRegistryVersion)
    {
        SearchCertification certification = Certification(paths:
        [
            CanonicalOnly(ObservationSubjectPatient, operations, coveredRegistryVersion),
            CanonicalOnly(PatientOrganization, operations, coveredRegistryVersion),
        ]);
        return TestSearch.Planner(Capabilities(certification, certification, registry: registry));
    }

    [Fact]
    public void AdmitsCanonicalOnlyEmissionAttestedForTheCurrentAliasedRegistry()
    {
        ShardRegistry registry = Aliased();
        SearchPlanner planner = Planner(registry, ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch, registry.Version);

        ReferenceHop hop = PlanOk("Observation", "subject:Patient.name=smith", planner).Occurrences[0].Hop.ShouldNotBeNull();
        hop.Mode.ShouldBe(ChainResolutionMode.Distributed);
        hop.Certifications.Values.ShouldAllBe(c => c.ForeignForms == ReferenceSearchForms.CanonicalAbsolute && c.CoveredRegistryVersion == registry.Version);
        PlanOk("Patient", "_revinclude=Observation:subject", planner).Occurrences[0].Hop!.Path.ShouldBe(ObservationSubjectPatient);
    }

    // Coverage attested before the aliases were registered says nothing about references stored under them.
    [Fact]
    public void RefusesAReferenceSearchRewriteAttestedForAnotherRegistry()
    {
        SearchPlanner planner = Planner(Aliased(), ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch, Registry().Version);

        ShouldFail("Observation", "subject:Patient.name=smith", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    [Fact]
    public void RefusesAReferenceSearchRewriteWhenOneShardAttestsAnotherRegistry()
    {
        ShardRegistry registry = Aliased();
        SearchCertification current = Certification(paths: [FullPath(ObservationSubjectPatient, registry.Version)]);
        SearchCertification stale = Certification(paths: [FullPath(ObservationSubjectPatient, Registry().Version)]);
        SearchPlanner planner = TestSearch.Planner(Capabilities(current, stale, registry: registry));

        ShouldFail("Observation", "subject:Patient.name=smith", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    // Registry coverage binds only the rewrites that emit reference values; the shard's own native proofs stand.
    [Fact]
    public void KeepsOperationsThatEmitNoReferenceValuesWhenCoverageIsStale()
    {
        ShardRegistry registry = Aliased();
        SearchCertification stale = Certification(paths: [FullPath(ObservationSubjectPatient, Registry().Version), FullPath(PatientOrganization, Registry().Version)]);
        SearchPlanner planner = TestSearch.Planner(Capabilities(stale, stale, registry: registry), "Observation.subject:Patient");

        PlanOk("Observation", "subject:Patient.name=smith", planner).Occurrences[0].Hop!.Mode.ShouldBe(ChainResolutionMode.Passthrough);
        PlanOk("Patient", "_include=Patient:organization", planner).Occurrences[0].Hop!.Path.ShouldBe(PatientOrganization);
    }

    [Fact]
    public void ADistributedForwardProofDoesNotAdmitARevInclude()
    {
        ShardRegistry registry = Aliased();
        SearchPlanner planner = Planner(registry, ReferencePathOperations.DistributedForward, registry.Version);

        PlanOk("Observation", "subject:Patient.name=smith", planner);
        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: planner);
    }

    [Fact]
    public void ARevIncludeProofDoesNotAdmitADistributedForwardChain()
    {
        ShardRegistry registry = Aliased();
        SearchPlanner planner = Planner(registry, ReferencePathOperations.RevIncludeSearch, registry.Version);

        PlanOk("Patient", "_revinclude=Observation:subject", planner);
        ShouldFail("Observation", "subject:Patient.name=smith", SearchFailureCode.UnsupportedChain, planner: planner);
    }

    [Theory]
    [InlineData("code=1")]
    [InlineData("date=ge2025-01-01")]
    [InlineData("_sort=_lastUpdated")]
    [InlineData("subject:Patient.gender=male")]
    public void StandaloneReferenceSearchProofDoesNotCoverAnAdditionalKeptFilter(string kept)
    {
        SearchPlanner planner = TestSearch.Planner();

        PlanOk("Observation", "subject:Patient.name=smith", planner);
        ShouldFail("Observation", $"subject:Patient.name=smith&{kept}", SearchFailureCode.UnsupportedChain, planner: planner);
    }

    [Fact]
    public void ForwardShapeEvidenceCoversOnlyTheRetainedValuesProvenOnEveryShard()
    {
        const string shape = "Observation?@Observation.subject:Patient&date=ge2025-01-01";
        const string search = "subject:Patient.name=smith&date=ge2025-01-01";
        SearchCertification proven = Certification(paths: [FullPath(ObservationSubjectPatient, forwardShapes: [shape])]);
        SearchPlanner planner = TestSearch.Planner(Capabilities(proven, proven));

        PlanOk("Observation", search, planner);
        PlanOk("Observation", "subject:Patient.name=jones&date=%67e2025-01-01", planner);
        ShouldFail("Observation", "subject:Patient.name=smith&date=lt2025-01-01", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Observation", "subject:Patient.name=smith&date=ge2026-01-01", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Observation", search, SearchFailureCode.UnsupportedChain, planner: TestSearch.Planner(Capabilities(proven)));
    }

    [Fact]
    public void HitShapeAndExactCountProofDoNotCoverReferenceSelectionInACountSearch()
    {
        SearchCertification hitsOnly = Certification(exactCountShapes: ["Observation?subject=,"]);
        SearchPlanner planner = TestSearch.Planner(Capabilities(hitsOnly, hitsOnly));

        PlanOk("Observation", "subject:Patient.name=smith", planner);
        ShouldFail("Observation", "subject:Patient.name=smith&_summary=count", SearchFailureCode.UnsupportedChain, planner: planner);
        ShouldFail("Observation", "subject:Patient.name=smith&_total=accurate", SearchFailureCode.UnsupportedChain, planner: planner);
    }

    [Fact]
    public void RevIncludeProofCoversItsFollowUpSearchNotThePrimaryFilters()
    {
        PlanOk("Patient", "name=smith&_revinclude=Observation:subject");
        PlanOk("Patient", "gender=male&_revinclude=Observation:subject");
    }

    [Fact]
    public void DoesNotAdmitOrAdvertiseRevIncludeWithoutItsStandaloneProof()
    {
        var path = new ReferencePathCertification(
            ObservationSubjectPatient,
            ReferencePathOperations.RevIncludeSearch,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            Registry().Version);
        SearchCapabilities capabilities = Capabilities(west: Certification(paths: [path]));

        ShouldFail("Patient", "_revinclude=Observation:subject", SearchFailureCode.UnsupportedInclude, planner: TestSearch.Planner(capabilities));
        capabilities.Resources.Single(r => r.Type.Value == "Patient").SearchRevIncludes.ShouldNotContain("Observation:subject");
    }

    [Fact]
    public void RefusedShapeDoesNotRenderCallerValuesOrProofLiteralsInDiagnostics()
    {
        var certificate = FullPath(ObservationSubjectPatient, forwardShapes: ["Observation?@Observation.subject:Patient&code=operator-literal"]);
        SearchCertification shard = Certification(paths: [certificate]);
        SearchPlanner planner = TestSearch.Planner(Capabilities(shard, shard));
        SearchQuery query = Query("subject:Patient.name=caller-name&code=caller-identifier");
        QueryOccurrence kept = query.Occurrences[1];
        SearchFailure failure = planner.Plan(Type("Observation"), query).Failure.ShouldNotBeNull();

        failure.ToString().ShouldNotContain("caller-identifier");
        failure.ToString().ShouldNotContain("caller-name");
        certificate.ToString().ShouldNotContain("operator-literal");
        kept.ToString().ShouldNotContain("caller-identifier");
        kept.Raw.ToString().ShouldNotContain("caller-identifier");
    }
}

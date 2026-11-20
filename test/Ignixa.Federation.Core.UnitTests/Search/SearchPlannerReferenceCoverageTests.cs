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
        coveredRegistryVersion);

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
}

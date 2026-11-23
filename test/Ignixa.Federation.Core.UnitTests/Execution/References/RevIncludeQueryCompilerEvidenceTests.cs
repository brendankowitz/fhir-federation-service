using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeScenario;
using Ignixa.Federation.Core.UnitTests.Search;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// A queried shard is sent only what its own current certification proves, and every request must fit before any is returned.
/// </summary>
public sealed class RevIncludeQueryCompilerEvidenceTests
{
    private static readonly string StaleVersion = new('0', 64);

    private static readonly ResourceKey[] Targets = [Key(East, "a"), Key(West, "a")];

    private sealed class FitProbe(Func<ShardSearchRequest, bool>? decide = null)
    {
        public List<ShardSearchRequest> Seen { get; } = [];

        public bool Fits(ShardSearchRequest request)
        {
            Seen.Add(request);
            return decide?.Invoke(request) ?? true;
        }
    }

    private static RevIncludeCompilationException Unsupported(
        (QueryPlan Plan, PlannedOccurrence Occurrence) planned,
        ResourceKey[] targets,
        ShardId expectedShard)
    {
        var probe = new FitProbe();
        RevIncludeCompilationException exception = Should.Throw<RevIncludeCompilationException>(
            () => RevIncludeQueryCompiler.Compile(planned.Plan, planned.Occurrence, targets, [East, West], 50, probe.Fits));
        exception.Reason.ShouldBe(RevIncludeCompilationReason.UnsupportedEvidence);
        exception.Shard.ShouldBe(expectedShard);
        probe.Seen.ShouldBeEmpty();
        return exception;
    }

    private static (QueryPlan Plan, PlannedOccurrence Occurrence) Certified(
        ShardRegistry registry,
        ReferencePathCertification? east,
        ReferencePathCertification? west)
    {
        var certifications = new Dictionary<ShardId, ReferencePathCertification>();
        if (east is not null)
        {
            certifications.Add(East, east);
        }

        if (west is not null)
        {
            certifications.Add(West, west);
        }

        return Recertified(Plan(registry), registry, certifications);
    }

    [Fact]
    public void RefusesAShardWithoutACertification()
    {
        ShardRegistry registry = Registry();

        Unsupported(Certified(registry, EastCertificate(registry), null), Targets, West);
    }

    [Fact]
    public void RefusesAShardThatDoesNotProveTheReverseIncludeSearch()
    {
        ShardRegistry registry = Registry();
        ReferencePathCertification forwardOnly = Certify(
            TestSearch.ObservationSubjectPatient,
            ReferenceSearchForms.Relative,
            ReferenceSearchForms.CanonicalAbsolute,
            registry.Version,
            ReferencePathOperations.DistributedForward);

        Unsupported(Certified(registry, forwardOnly, WestCertificate(registry)), Targets, East);
    }

    // Right path, forms and registry version, and the operation is certified, but without its standalone source search shape.
    [Fact]
    public void RefusesAShardThatProvesTheOperationWithoutTheStandaloneShape()
    {
        ShardRegistry registry = Registry();
        ReferencePathCertification shapeless = Certify(
            TestSearch.ObservationSubjectPatient,
            ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            registry.Version,
            withShape: false);
        shapeless.Operations.ShouldBe(ReferencePathOperations.RevIncludeSearch);

        Unsupported(Certified(registry, shapeless, WestCertificate(registry)), Targets, East);
    }

    [Fact]
    public void RefusesAShardWhoseCoverageIsForAnotherRegistry()
    {
        ShardRegistry registry = Registry();
        ReferencePathCertification stale = Certify(
            TestSearch.ObservationSubjectPatient,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            StaleVersion);

        Unsupported(Certified(registry, EastCertificate(registry), stale), Targets, West);
    }

    [Fact]
    public void RefusesAShardWhoseProofIsForAnotherPath()
    {
        ShardRegistry registry = Registry();
        ReferencePathCertification otherPath = Certify(
            TestSearch.PatientOrganization,
            ReferenceSearchForms.CanonicalAbsolute,
            ReferenceSearchForms.CanonicalAbsolute,
            registry.Version);

        Unsupported(Certified(registry, otherPath, WestCertificate(registry)), Targets, East);
    }

    // East certifies only link base aliases for its own keys and has none, so an east key has no spelling on east.
    [Fact]
    public void RefusesAKeyNoCertifiedFormSpells()
    {
        ShardRegistry registry = Registry();
        ReferencePathCertification aliasesOnly = Certify(
            TestSearch.ObservationSubjectPatient,
            ReferenceSearchForms.LinkBaseAliases,
            ReferenceSearchForms.CanonicalAbsolute,
            registry.Version);
        (QueryPlan, PlannedOccurrence) planned = Certified(registry, aliasesOnly, WestCertificate(registry));

        Unsupported(planned, [Key(East, "a")], East);

        // The same shard spells a key it does not own, so only the unspellable key refuses the search.
        ShardSearchRequest request = RevIncludeQueryCompiler.Compile(planned.Item1, planned.Item2, [Key(West, "a")], [East], 50, _ => true).ShouldHaveSingleItem();
        request.Decoded().ShouldBe("https://west.example/fhir/Patient/a");
    }

    [Fact]
    public void JudgesOnlyTheEvidenceOfTheShardsQueried()
    {
        ShardRegistry registry = Registry();
        (QueryPlan Plan, PlannedOccurrence Occurrence) planned = Certified(registry, EastCertificate(registry), null);

        RevIncludeQueryCompiler.Compile(planned.Plan, planned.Occurrence, Targets, [East], 50, _ => true).ShouldHaveSingleItem().Shard.ShouldBe(East);
        Unsupported(planned, Targets, West);
    }
}

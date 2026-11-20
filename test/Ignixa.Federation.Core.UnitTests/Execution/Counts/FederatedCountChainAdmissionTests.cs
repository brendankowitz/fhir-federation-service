using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Counts.CountFixture;
using static Ignixa.Federation.Core.UnitTests.Search.TestSearch;

namespace Ignixa.Federation.Core.UnitTests.Execution.Counts;

/// <summary>
/// A chain's count is exact only where its hop is certified, so the plan's hop certifications must cover the executor's
/// registry, and a reference rewrite's coverage must be attested for its version (design spec §7.5).
/// </summary>
public sealed class FederatedCountChainAdmissionTests
{
    private const string DistributedForward = "distributed forward";

    private const string PlannedCountShape = "count:Observation?@Observation.subject:Patient";

    private const ReferencePathOperations AllOperations = ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse
        | ReferencePathOperations.DistributedForward | ReferencePathOperations.DistributedReverse
        | ReferencePathOperations.IncludeExtraction | ReferencePathOperations.RevIncludeSearch;

    private readonly FakeShardClient _client = new();

    public static TheoryData<string> Chains => [DistributedForward, "distributed reverse", "passthrough forward", "passthrough reverse"];

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task ShardAddedSinceThePlanIsRefusedEvenWhenItCertifiesTheCountShape(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry wider = TestShards.Create(TestShards.East(), TestShards.West(), TestShards.Shard("north", "n4rt", "https://north.example/fhir"));
        SearchCapabilities capabilities = CertifiedOn(wider, shape);
        ShardCountTarget[] targets = [.. wider.Shards.Select(s => ShardCountTarget.Search(s.Id, ShardSearchMethod.Get, replacements))];

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => Executor(_client, capabilities).ExecuteAsync(plan, targets, 3, Token));

        _client.Calls.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task ShardRemovedSinceThePlanIsRefused(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry narrower = TestShards.Create(TestShards.East());
        SearchCapabilities capabilities = CertifiedOn(narrower, shape);
        _client.Answer(East, 2);

        await Should.ThrowAsync<ExactCountNotCertifiedException>(
            () => Executor(_client, capabilities).ExecuteAsync(plan, [ShardCountTarget.Search(East, ShardSearchMethod.Get, replacements)], 1, Token));

        _client.Calls.ShouldBeEmpty();
    }

    /// <summary>
    /// coverage attested for the registry version; the other chains still count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Chains))]
    public async Task RegistryVersionChangedSinceThePlanRefusesOnlyAReferenceRewrite(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ShardRegistry moved = TestShards.Create(TestShards.East(), TestShards.Shard("west", "p9xm", "https://west2.example/fhir"));
        moved.Version.ShouldNotBe(Registry().Version);
        SearchCapabilities capabilities = CertifiedOn(moved, shape);
        ShardCountTarget[] targets = [.. moved.Shards.Select(s => ShardCountTarget.Search(s.Id, ShardSearchMethod.Get, replacements))];
        _client.Answer(East, 2);
        _client.Answer(West, 3);

        Task<int?> counting = Executor(_client, capabilities).ExecuteAsync(plan, targets, 2, Token);

        if (chain == DistributedForward)
        {
            await Should.ThrowAsync<ExactCountNotCertifiedException>(counting);
            _client.Calls.ShouldBeEmpty();
        }
        else
        {
            (await counting).ShouldBe(5);
        }
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task HopEvidenceMissingFromTheExecutorsCapabilitiesIsRefusedEvenWhenTheCountShapeIsCertified(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        SearchCapabilities capabilities = CertifiedWith(shape);

        await ShouldRefuse(plan, capabilities, replacements);
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task HopOperationNoLongerCertifiedIsRefused(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        ReferencePathOperations required = chain switch
        {
            DistributedForward => ReferencePathOperations.DistributedForward,
            "distributed reverse" => ReferencePathOperations.DistributedReverse,
            "passthrough forward" => ReferencePathOperations.NativeForward,
            _ => ReferencePathOperations.NativeReverse,
        };
        SearchCapabilities capabilities = CertifiedWith(shape, SubjectHop(AllOperations & ~required));

        await ShouldRefuse(plan, capabilities, replacements);
    }

    [Fact]
    public async Task ReferenceRewriteWhosePlannedCountShapeIsNoLongerCertifiedIsRefused()
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(DistributedForward);
        SearchCapabilities capabilities = CertifiedWith(shape, SubjectHop(forwardShapes: ["Observation?@Observation.subject:Patient"]));

        await ShouldRefuse(plan, capabilities, replacements);
    }

    /// <summary>
    /// The resolved references were emitted in the planned forms, so a reference rewrite needs them unchanged; the other
    /// chains send no reference values, so their forms are not evidence they rely on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Chains))]
    public async Task ChangedReferenceFormsRefuseOnlyAReferenceRewrite(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        SearchCapabilities capabilities = CertifiedWith(shape, SubjectHop(ownSideForms: ReferenceSearchForms.CanonicalAbsolute));

        if (chain == DistributedForward)
        {
            await ShouldRefuse(plan, capabilities, replacements);
        }
        else
        {
            (await Count(plan, capabilities, replacements)).ShouldBe(5);
        }
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public async Task EquivalentButSeparatelyBuiltEvidenceCounts(string chain)
    {
        (QueryPlan plan, string shape, IReadOnlyDictionary<int, string> replacements) = PlanBeforeTheChange(chain);
        SearchCapabilities capabilities = CertifiedWith(shape, SubjectHop());

        (await Count(plan, capabilities, replacements)).ShouldBe(5);
        _client.Calls.Count.ShouldBe(2);
    }

    private async Task ShouldRefuse(QueryPlan plan, SearchCapabilities capabilities, IReadOnlyDictionary<int, string> replacements)
    {
        await Should.ThrowAsync<ExactCountNotCertifiedException>(() => Count(plan, capabilities, replacements));
        _client.Calls.ShouldBeEmpty();
    }

    /// <summary>Counts on east (2) and west (3) of the default registry, the registry the plan was made under.</summary>
    private Task<int?> Count(QueryPlan plan, SearchCapabilities capabilities, IReadOnlyDictionary<int, string> replacements)
    {
        _client.Answer(East, 2);
        _client.Answer(West, 3);
        ShardCountTarget[] targets = [.. capabilities.Registry.Shards.Select(s => ShardCountTarget.Search(s.Id, ShardSearchMethod.Get, replacements))];
        return Executor(_client, capabilities).ExecuteAsync(plan, targets, 2, Token);
    }

    /// <summary>
    /// East and west of the default registry, same ids and version as the plan's, each certifying <paramref name="shape"/>
    /// exact and only the given <paramref name="paths"/>; new objects, never the planner's.
    /// </summary>
    private static SearchCapabilities CertifiedWith(string shape, params ReferencePathCertification[] paths)
    {
        ShardRegistry registry = Registry();
        SearchCertification certification = Certification(exactCountShapes: [shape], paths: paths);
        return new SearchCapabilities(registry, registry.Shards.Select(s => new ShardSearchCapability(s.Id, AllResources, certification)));
    }

    /// <summary>
    /// The subject hop as the plan saw it unless overridden. Revinclude search is always kept, so forms and coverage stay
    /// valid; forward shapes are kept only while distributed forward is proven.
    /// </summary>
    private static ReferencePathCertification SubjectHop(
        ReferencePathOperations operations = AllOperations,
        ReferenceSearchForms ownSideForms = ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute,
        string[]? forwardShapes = null) => new(
        ObservationSubjectPatient,
        operations,
        ownSideForms,
        ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases,
        Registry().Version,
        operations.HasFlag(ReferencePathOperations.DistributedForward) ? forwardShapes ?? [PlannedCountShape] : [],
        StandaloneShape(ObservationSubjectPatient));

    /// <summary>Plans a count of <paramref name="chain"/> on east and west under the default registry.</summary>
    private static (QueryPlan Plan, string Shape, IReadOnlyDictionary<int, string> Replacements) PlanBeforeTheChange(string chain)
    {
        (string type, string query, string shape, string[] passthrough, string? replacement) = chain switch
        {
            DistributedForward => ("Observation", "subject:Patient.name=x&_summary=count", "Observation?subject=,", Array.Empty<string>(), "Patient/a"),
            "distributed reverse" => ("Patient", "_has:Observation:subject:code=1&_summary=count", "Patient?_id=,", [], "a"),
            "passthrough forward" => ("Observation", "subject:Patient.name=x&_summary=count", "Observation?subject:Patient.name", ["Observation.subject:Patient"], null),
            _ => ("Patient", "_has:Observation:subject:code=1&_summary=count", "Patient?_has:Observation:subject:code", ["Observation.subject:Patient"], null),
        };
        SearchCapabilities capabilities = CertifiedOn(Registry(), shape);
        QueryPlan plan = PlanOk(type, query, Planner(capabilities, passthrough));
        IReadOnlyDictionary<int, string> replacements = replacement is null ? NoReplacements : new Dictionary<int, string> { [0] = replacement };
        return (plan, shape, replacements);
    }

    /// <summary>Every shard of <paramref name="registry"/> certifies <paramref name="shape"/> exact and the subject hop for its version.</summary>
    private static SearchCapabilities CertifiedOn(ShardRegistry registry, string shape)
    {
        SearchCertification certification = Certification(
            exactCountShapes: [shape],
            paths: [FullPath(ObservationSubjectPatient, registry.Version, ["count:Observation?@Observation.subject:Patient"])]);
        return new SearchCapabilities(registry, registry.Shards.Select(s => new ShardSearchCapability(s.Id, AllResources, certification)));
    }
}

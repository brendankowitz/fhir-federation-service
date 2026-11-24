using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Chains;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution.Chains;

/// <summary>
/// Two shards, east and west, with operation-specific forward certificates that differ by queried shard: east emits its own
/// keys relative and foreign keys canonical; west emits its own keys canonical and foreign keys canonical plus link base
/// aliases. East's alias holds <c>,</c> and <c>$</c>, so spelling it exercises FHIR escaping before percent encoding.
/// </summary>
internal static class ChainScenario
{
    public const string EastAlias = "https://east-alias.example/fhir,v$2";

    public static readonly ResourceType Patient = TestSearch.Type("Patient");
    public static readonly ResourceType Observation = TestSearch.Type("Observation");
    public static readonly ResourceType Practitioner = TestSearch.Type("Practitioner");
    public static readonly ShardId East = ShardId.Parse("east");
    public static readonly ShardId West = ShardId.Parse("west");

    /// <summary>Every primary search shape the scenario's tests plan, in outbound order.</summary>
    public static readonly string[] Shapes =
    [
        "Observation?@Observation.subject:Patient",
        "Observation?@Observation.subject:Patient&code=a%2Cb&@Observation.performer:Practitioner",
        "Observation?@Observation.subject:Patient&@Observation.performer:Practitioner",
        "Observation?@Observation.subject:Patient&@Observation.subject:Patient",
        "Observation?code=a&code=b&@Observation.subject:Patient",
        "Observation?code=a&code=A&@Observation.subject:Patient",
        "Observation?code=a&code=a&@Observation.subject:Patient",
        "Observation?code=a%2Cb&code=a%2Cb&@Observation.subject:Patient",
        "Observation?code=a%20b&code=a%20b&@Observation.subject:Patient",
        "Observation?code=a%20b&@Observation.subject:Patient&code=a%20b",
        "Observation?code=a%2Bb&@Observation.subject:Patient&code=a%2Bb",
        "Observation?code=a%7Cb&@Observation.subject:Patient&@Observation.subject:Patient",
        "Observation?code=abc&@Observation.subject:Patient&code=abc",
        "Observation?code=a%20b&@Observation.subject:Patient",
    ];

    public static IReadOnlySet<ResourceType> AllReadable { get; } = new HashSet<ResourceType> { Patient, Observation, Practitioner };

    public static ShardRegistry Registry() => TestShards.Create(TestShards.East(EastAlias), TestShards.West());

    /// <summary>A profile without POST search, whose largest page differs from the stock profiles'.</summary>
    public static readonly BackendProfile GetOnly = new("GetOnly", SupportsPostSearch: false, MaxCount: 500, NextLinkRedemption.FollowGet, null);

    /// <summary>The scenario's registry with east on the <see cref="GetOnly"/> profile.</summary>
    public static ShardRegistry GetOnlyEastRegistry() =>
        ShardRegistry.Create([TestShards.East(EastAlias).WithProfile(GetOnly.Name), TestShards.West()], [.. TestShards.Profiles, GetOnly]);

    /// <summary>A POST-capable profile whose largest page is smaller than the default page size, so a primary count is clamped.</summary>
    public static readonly BackendProfile SmallPost = new("SmallPost", SupportsPostSearch: true, MaxCount: 10, NextLinkRedemption.FollowGet, null);

    /// <summary>East on <see cref="GetOnly"/> (GET, largest page 500) and west on <see cref="SmallPost"/> (POST, largest page 10), without aliases.</summary>
    public static ShardRegistry MixedRegistry() =>
        ShardRegistry.Create([TestShards.East().WithProfile(GetOnly.Name), TestShards.West().WithProfile(SmallPost.Name)], [.. TestShards.Profiles, GetOnly, SmallPost]);

    /// <summary>The scenario's shards configured west first, so west has ordinal 0 and its stream comes first.</summary>
    public static ShardRegistry WestFirstRegistry() => TestShards.Create(TestShards.West(), TestShards.East(EastAlias));

    /// <summary>Gets a bound of <paramref name="maxRequestBytes"/> for each method <paramref name="shard"/>'s profile can be sent.</summary>
    public static ShardRequestBound ProfileBound(ShardDescriptor shard, int maxRequestBytes = 1_000_000) =>
        new(maxRequestBytes, shard.Profile.SupportsPostSearch ? maxRequestBytes : null);

    /// <summary>Gets a witness page with <paramref name="ids"/> of <paramref name="type"/> on <paramref name="shard"/>.</summary>
    public static ShardPage Witness(ShardId shard, ResourceType type, ShardCursor? next, params string[] ids) =>
        new(ids.Select(id => Fixture.Entry(Key(shard, type, id))), next, null);

    public static SearchPlanner Planner(ShardRegistry registry, params string[] passthrough)
    {
        var chains = new ChainResolutionOptions();
        foreach (string path in passthrough)
        {
            chains.Passthrough.Add(path);
        }

        return new SearchPlanner(Capabilities(registry), chains, new LimitOptions());
    }

    /// <summary>Gets the scenario's capabilities; <paramref name="westOwnSide"/> replaces west's own-side forms.</summary>
    public static SearchCapabilities Capabilities(ShardRegistry registry, ReferenceSearchForms westOwnSide = ReferenceSearchForms.CanonicalAbsolute) => new(
        registry,
        registry.Shards.Select(s => new ShardSearchCapability(
            s.Id,
            TestSearch.AllResources,
            TestSearch.Certification(
                paths:
                [
                    Forward(TestSearch.ObservationSubjectPatient, registry, s.Id, westOwnSide),
                    Forward(TestSearch.ObservationPerformerPractitioner, registry, s.Id, westOwnSide),
                ]), "4.0.1")), TestSearch.R4Specification);

    public static QueryPlan Plan(ShardRegistry registry, string encoded, params string[] passthrough) =>
        TestSearch.PlanOk("Observation", encoded, Planner(registry, passthrough));

    /// <summary>Plans <paramref name="encoded"/> with west certifying <paramref name="westOwnSide"/> for its own keys.</summary>
    public static QueryPlan Plan(ShardRegistry registry, ReferenceSearchForms westOwnSide, string encoded) =>
        TestSearch.PlanOk("Observation", encoded, new SearchPlanner(Capabilities(registry, westOwnSide), new ChainResolutionOptions(), new LimitOptions()));

    public static ForwardChainResolver Resolver(
        IShardClient client,
        ShardRegistry registry,
        LimitOptions? limits = null,
        TimeoutOptions? timeouts = null,
        Func<ShardDescriptor, ShardRequestBound>? bound = null) =>
        new(
            client,
            registry,
            TestSearch.R4Specification,
            registry.Shards.ToDictionary(s => s.Id, s => bound?.Invoke(s) ?? ProfileBound(s)),
            limits ?? new LimitOptions(),
            timeouts ?? new TimeoutOptions());

    /// <summary>Creates a resolver that measures every first-sent request with the host's <paramref name="fitsRequest"/>.</summary>
    public static ForwardChainResolver Resolver(
        IShardClient client,
        ShardRegistry registry,
        Func<ShardSearchRequest, bool> fitsRequest) =>
        new(client, registry, TestSearch.R4Specification, new LimitOptions(), new TimeoutOptions(), fitsRequest);

    public static Task<ForwardChainResolution> ResolveAsync(
        this ForwardChainResolver resolver,
        QueryPlan plan,
        CancellationToken cancellationToken,
        int fetchBudget = 100,
        long payloadBytes = 1_000_000) =>
        resolver.ResolveAsync(plan, AllReadable, fetchBudget, payloadBytes, cancellationToken);

    public static ResourceKey Key(ShardId shard, ResourceType type, string id) => new(shard, type, ResourceId.Parse(id));

    /// <summary>Percent-decodes a primary request's parameter, as a backend reads it.</summary>
    public static string Decoded(this ShardSearchRequest request, string key) =>
        Uri.UnescapeDataString(request.Parameters.Single(p => p.Key == key).Value);

    public static ShardSearchRequest Stream(this ForwardChainReady ready, ShardId shard) => ready.Streams.Single(s => s.Shard == shard);

    public static Task<UnsortedPage> ExecuteAsync(this ForwardChainReady ready, IShardClient client, ShardRegistry registry, CancellationToken cancellationToken) =>
        new UnsortedPageExecutor(client, registry, 4).ExecuteAsync(ready.Streams, ready.PageSize, null, ready.RemainingFetchBudget, cancellationToken);

    private static ReferencePathCertification Forward(ReferencePath path, ShardRegistry registry, ShardId queried, ReferenceSearchForms westOwnSide) =>
        queried == East
            ? new(
                path,
                ReferencePathOperations.NativeForward | ReferencePathOperations.DistributedForward | ReferencePathOperations.IncludeExtraction,
                ReferenceSearchForms.Relative,
                ReferenceSearchForms.CanonicalAbsolute,
                registry.Version,
                Shapes)
            : new(
                path,
                ReferencePathOperations.NativeForward | ReferencePathOperations.DistributedForward | ReferencePathOperations.IncludeExtraction,
                westOwnSide,
                ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases,
                registry.Version,
                Shapes);
}

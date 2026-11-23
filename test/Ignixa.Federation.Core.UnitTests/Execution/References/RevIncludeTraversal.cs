using System.Text;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// Plans real reverse includes against two shards and scripts their source searches. East (ordinal 0) is searched by POST
/// up to 1000 per page and redeems cursors by reposting; west (ordinal 1) only by GET, up to 3 per page, following cursors.
/// Patient reverse-includes Observation and Encounter by <c>subject</c>; Organization reverse-includes itself by <c>partof</c>.
/// </summary>
internal static class RevIncludeTraversal
{
    public const string Observations = "_revinclude=Observation:subject";
    public const string Encounters = "_revinclude=Encounter:subject";
    public const string Both = Observations + "&" + Encounters;

    public static readonly ShardId East = RevIncludeScenario.East;
    public static readonly ShardId West = RevIncludeScenario.West;
    public static readonly ResourceType Patient = RevIncludeScenario.Patient;
    public static readonly ResourceType Observation = RevIncludeScenario.Observation;
    public static readonly ResourceType Encounter = TestSearch.Type("Encounter");
    public static readonly ResourceType Organization = TestSearch.Type("Organization");

    public static readonly BackendProfile SmallGet = new("RevSmallGet", SupportsPostSearch: false, MaxCount: 3, NextLinkRedemption.FollowGet, null);

    private static readonly ReferencePath[] Paths =
    [
        TestSearch.ObservationSubjectPatient,
        TestSearch.Path("Encounter", "subject", "Patient"),
        TestSearch.PatientOrganization,
        TestSearch.Path("Organization", "partof", "Organization"),
    ];

    private static readonly ResourceSearchSupport[] Resources =
    [
        new(
            Patient,
            [TestSearch.Param("_id", SearchParamType.Token), TestSearch.Param("organization", SearchParamType.Reference)],
            ["Patient:organization"],
            ["Observation:subject", "Encounter:subject"]),
        new(Observation, [TestSearch.Param("_id", SearchParamType.Token), TestSearch.Param("subject", SearchParamType.Reference)], [], []),
        new(Encounter, [TestSearch.Param("_id", SearchParamType.Token), TestSearch.Param("subject", SearchParamType.Reference)], [], []),
        new(
            Organization,
            [TestSearch.Param("_id", SearchParamType.Token), TestSearch.Param("partof", SearchParamType.Reference)],
            [],
            ["Organization:partof"]),
    ];

    // Lets a bare count-only search plan (design spec §7.2).
    private static readonly string[] CountShapes = ["Patient?"];

    public static ShardRegistry Registry() =>
        ShardRegistry.Create(
            [TestShards.East(), TestShards.West("https://west-legacy.example/fhir").WithProfile(SmallGet.Name)],
            [.. TestShards.Profiles, SmallGet]);

    /// <summary>Plans <paramref name="encoded"/> on <paramref name="type"/>, every path certified by both shards for <paramref name="registry"/>.</summary>
    public static QueryPlan Plan(ShardRegistry registry, string encoded, string type = "Patient")
    {
        ReferencePathCertification[] Certified(ReferenceSearchForms own, ReferenceSearchForms foreign) =>
        [
            .. Paths.Select(path => new ReferencePathCertification(
                path,
                ReferencePathOperations.RevIncludeSearch | ReferencePathOperations.IncludeExtraction,
                own,
                foreign,
                registry.Version,
                null,
                TestSearch.StandaloneShape(path))),
        ];

        var capabilities = new SearchCapabilities(
            registry,
            [
                new ShardSearchCapability(
                    East,
                    Resources,
                    TestSearch.Certification(
                        exactCountShapes: CountShapes,
                        paths: Certified(ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute, ReferenceSearchForms.CanonicalAbsolute))),
                new ShardSearchCapability(
                    West,
                    Resources,
                    TestSearch.Certification(
                        exactCountShapes: CountShapes,
                        paths: Certified(ReferenceSearchForms.CanonicalAbsolute, ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases))),
            ]);
        return TestSearch.PlanOk(type, encoded, TestSearch.Planner(capabilities));
    }

    public static RevIncludePageExecutor Executor(IShardClient client, ShardRegistry registry, int maxParallelFetches = 1, Func<ShardSearchRequest, bool>? fits = null) =>
        new(client, registry, maxParallelFetches, fits ?? (_ => true));

    public static IReadOnlySet<ResourceType> Readable(params ResourceType[] types) => types.ToHashSet();

    public static IReadOnlySet<ResourceType> ReadAll { get; } = Readable(Patient, Observation, Encounter, Organization);

    public static ResourceKey Key(ShardId shard, ResourceType type, string id) => new(shard, type, ResourceId.Parse(id));

    /// <summary>An entry whose bytes name <paramref name="tag"/>, so a test can tell which copy of a resource won.</summary>
    public static ShardEntry Entry(ShardId shard, ResourceType type, string id, string? version = "1", string tag = "") =>
        new(Key(shard, type, id), version, null, Encoding.UTF8.GetBytes($"{{\"resourceType\":\"{type}\",\"id\":\"{id}\",\"tag\":\"{tag}\"}}"));

    public static ShardEntry Match(ShardId shard, string id, ResourceType? type = null) => Entry(shard, type ?? Patient, id);

    public static ShardPage Page(ShardCursor? next, params ShardEntry[] entries) => new(entries, next, null);

    /// <summary>Gets a cursor of the stream <paramref name="type"/> on <paramref name="shard"/> starts, redeemed as that stream's method requires.</summary>
    public static ShardCursor Cursor(ShardRegistry registry, ShardId shard, ResourceType type, string target)
    {
        ShardDescriptor descriptor = registry.Shards.Single(s => s.Id == shard);
        ShardSearchMethod method = descriptor.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
        return new ShardCursor(shard, type, $"https://{shard}.example/fhir/{type}?ct={target}&subject=sarah-secret", descriptor.Profile.RedemptionFor(method));
    }

    /// <summary>Renders entries as <c>shard/type/id/version/tag</c>.</summary>
    public static string[] Rendered(this RevIncludePage page) =>
        [.. page.Includes.Select(e => $"{e.Key.Shard}/{e.Key.Type}/{e.Key.Id}/{e.VersionId ?? "-"}/{Tag(e)}")];

    private static string Tag(ShardEntry entry)
    {
        string json = Encoding.UTF8.GetString(entry.Resource.Span);
        string rest = json[(json.IndexOf("\"tag\":\"", StringComparison.Ordinal) + 7)..];
        return rest[..rest.IndexOf('"', StringComparison.Ordinal)];
    }
}

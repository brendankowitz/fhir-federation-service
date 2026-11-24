using System.Globalization;
using System.Text;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>
/// Builds matches, planned includes, registries and an <c>_id</c>-answering shard for forward include tests. Shards are
/// east (<c>https://east.example/fhir</c>) and west (<c>https://west.example/fhir</c>).
/// </summary>
internal static class IncludeFixture
{
    public static readonly ShardId East = ShardId.Parse("east");
    public static readonly ShardId West = ShardId.Parse("west");
    public static readonly ResourceType Observation = ResourceType.Parse("Observation");
    public static readonly ResourceType Patient = ResourceType.Parse("Patient");
    public static readonly ResourceType Organization = ResourceType.Parse("Organization");
    public static readonly ResourceType Practitioner = ResourceType.Parse("Practitioner");

    /// <summary>A backend that only searches with GET, so its <c>next</c> links are followed with GET.</summary>
    public static readonly BackendProfile GetOnly = new("GetOnly", SupportsPostSearch: false, MaxCount: 1000, NextLinkRedemption.FollowGet, null);

    public static IReadOnlySet<ResourceType> AllTypes { get; } = new HashSet<ResourceType> { Observation, Patient, Organization, Practitioner };

    /// <summary>Gets the planned <c>Observation:subject:Patient</c> include.</summary>
    public static PlannedOccurrence ObservationSubject { get; } = Include("Observation", "Observation:subject:Patient");

    /// <summary>Gets the planned <c>Observation:performer:Practitioner</c> include.</summary>
    public static PlannedOccurrence ObservationPerformer { get; } = Include("Observation", "Observation:performer:Practitioner");

    /// <summary>Gets the planned <c>Patient:organization</c> include.</summary>
    public static PlannedOccurrence PatientOrganization { get; } = Include("Patient", "Patient:organization");

    /// <summary>
    /// Gets the planned <c>Observation:has-member:Observation</c> include, a reference from a type to itself, planned by the
    /// real planner over shards that also advertise and certify it.
    /// </summary>
    public static PlannedOccurrence ObservationHasMember { get; } = Certified(
        "Observation:has-member:Observation",
        TestSearch.Path("Observation", "has-member", "Observation"),
        [
            .. TestSearch.AllResources.Select(r => r.Type == Observation
                ? new ResourceSearchSupport(
                    r.Type,
                    [.. r.Parameters, TestSearch.Param("has-member", SearchParamType.Reference)],
                    [.. r.SearchIncludes, "Observation:has-member"],
                    r.SearchRevIncludes)
                : r),
        ]);

    /// <summary>
    /// Gets the planned <c>Observation:patient:Patient</c> include, whose packaged expression filters with
    /// <c>resolve() is Patient</c>, planned by the real planner over shards that certify it. Observation already advertises
    /// the parameter and the searchInclude. Typed, because the planner refuses the untyped form as an unsupported include.
    /// </summary>
    public static PlannedOccurrence ObservationPatient { get; } = Certified(
        "Observation:patient:Patient",
        TestSearch.Path("Observation", "patient", "Patient"),
        TestSearch.AllResources);

    public static PlannedOccurrence Include(string type, string include) =>
        TestSearch.PlanOk(type, "_include=" + include).Occurrences.Single(o => o.Kind == OccurrenceKind.Include);

    /// <summary>
    /// Plans an Observation <paramref name="include"/> with the real planner over east and west shards that advertise
    /// <paramref name="resources"/> and also certify <paramref name="path"/>.
    /// </summary>
    private static PlannedOccurrence Certified(string include, ReferencePath path, IReadOnlyList<ResourceSearchSupport> resources)
    {
        SearchCertification certification = TestSearch.Certification(paths: [.. TestSearch.AllPaths, TestSearch.FullPath(path)]);
        var capabilities = new SearchCapabilities(
            TestSearch.Registry(),
            [new ShardSearchCapability(East, resources, certification, "4.0.1"), new ShardSearchCapability(West, resources, certification, "4.0.1")],
            TestSearch.R4Specification);
        return TestSearch.PlanOk("Observation", "_include=" + include, TestSearch.Planner(capabilities))
            .Occurrences.Single(o => o.Kind == OccurrenceKind.Include);
    }

    /// <summary>An Observation on <paramref name="shard"/> whose <c>hasMember</c> references are <paramref name="members"/>.</summary>
    public static ShardEntry Panel(ShardId shard, string id, params string[] members) => Resource(
        shard,
        Observation,
        id,
        "1",
        members.Length == 0 ? null : $"\"hasMember\":[{string.Join(",", members.Select(m => $"{{\"reference\":\"{m}\"}}"))}]");

    /// <summary>The east and west shards, both on the Microsoft FHIR server profile (POST search, max count 1000).</summary>
    public static ShardRegistry Registry(params string[] westLinkBaseAliases) =>
        TestShards.Create(TestShards.East(), TestShards.West(westLinkBaseAliases));

    /// <summary>The east and west shards on <paramref name="profile"/>.</summary>
    public static ShardRegistry Registry(BackendProfile profile) => ShardRegistry.Create(
        [TestShards.East().WithProfile(profile.Name), TestShards.West().WithProfile(profile.Name)],
        [TestShards.MicrosoftFhirServer, profile]);

    public static ForwardIncludeResolver Resolver(IShardClient client, ShardRegistry? registry = null, int maxIncludesPerPage = 1000) =>
        new(client, registry ?? Registry(), maxIncludesPerPage, TestSearch.R4Specification);

    /// <summary>Resolves with every test type authorized and a budget of 100 calls.</summary>
    public static Task<IncludeResult> ResolveAsync(
        this ForwardIncludeResolver resolver,
        IReadOnlyList<ShardEntry> matches,
        params PlannedOccurrence[] includes) =>
        resolver.ResolveAsync(matches, includes, AllTypes, 100, TestContext.Current.CancellationToken);

    public static ResourceKey Key(ShardId shard, ResourceType type, string id) => new(shard, type, ResourceId.Parse(id));

    /// <summary>A resource whose JSON holds its type, its id and <paramref name="members"/>.</summary>
    public static ShardEntry Resource(ShardId shard, ResourceType type, string id, string? version, string? members = null)
    {
        string json = $"{{\"resourceType\":\"{type.Value}\",\"id\":\"{id}\"{(string.IsNullOrEmpty(members) ? string.Empty : "," + members)}}}";
        return new ShardEntry(Key(shard, type, id), version, null, Encoding.UTF8.GetBytes(json));
    }

    /// <summary>An Observation with an optional subject reference and any number of performer references.</summary>
    public static ShardEntry Obs(ShardId shard, string id, string? subject, params string[] performers)
    {
        var members = new List<string>();
        if (subject is not null)
        {
            members.Add($"\"subject\":{{\"reference\":\"{subject}\"}}");
        }

        if (performers.Length > 0)
        {
            members.Add($"\"performer\":[{string.Join(",", performers.Select(p => $"{{\"reference\":\"{p}\"}}"))}]");
        }

        return Resource(shard, Observation, id, "1", string.Join(",", members));
    }

    /// <summary>An Observation on east whose subject is <paramref name="subject"/>.</summary>
    public static ShardEntry Obs(string id, string subject) => Obs(East, id, subject);

    public static ShardEntry Pat(ShardId shard, string id, string? version = "1") => Resource(shard, Patient, id, version);

    public static ShardEntry Raw(ShardId shard, ResourceType type, string id, byte[] resource) =>
        new(Key(shard, type, id), "1", null, resource);

    /// <summary>
    /// Makes <paramref name="shard"/> answer each <c>_id</c> search with the <paramref name="store"/> entries of the searched
    /// type whose id was requested, in store order, on one terminal page.
    /// </summary>
    public static void ServeIds(this FakeShardClient client, ShardId shard, params ShardEntry[] store) =>
        client.OnSearch(shard, (request, _) =>
        {
            HashSet<string> ids = [.. RequestedIds(request)];
            ShardEntry[] found = [.. store.Where(e => e.Key.Type == request.Type && ids.Contains(e.Key.Id.Value))];
            return Task.FromResult(new ShardPage(found, null, null));
        });

    /// <summary>Makes <paramref name="shard"/> answer every search with <paramref name="page"/>.</summary>
    public static void OnSearch(this FakeShardClient client, ShardId shard, ShardPage page) =>
        client.OnSearch(shard, (_, _) => Task.FromResult(page));

    /// <summary>
    /// Makes <paramref name="shard"/> answer its search with the first of <paramref name="pages"/> and each <c>next</c> link
    /// with the following one, redeemed the way <paramref name="profile"/> requires; the last page is terminal.
    /// </summary>
    public static void ServePages(this FakeShardClient client, ShardId shard, BackendProfile profile, params ShardEntry[][] pages) =>
        client.OnSearch(shard, (request, _) => Task.FromResult(Chain(client, request, profile, pages, 0)));

    private static ShardPage Chain(FakeShardClient client, ShardSearchRequest request, BackendProfile profile, ShardEntry[][] pages, int index)
    {
        if (index == pages.Length - 1)
        {
            return Page(null, pages[index]);
        }

        ShardCursor next = Cursor(request, profile, "page" + (index + 1).ToString(CultureInfo.InvariantCulture));
        client.OnContinue(next, _ => Task.FromResult(Chain(client, request, profile, pages, index + 1)));
        return Page(next, pages[index]);
    }

    /// <summary>Gets the ids of the request's single <c>_id</c> parameter, asserting it is the only parameter.</summary>
    public static string[] RequestedIds(ShardSearchRequest request)
    {
        ParameterOccurrence parameter = request.Parameters.ShouldHaveSingleItem();
        parameter.Key.ShouldBe("_id");
        return parameter.Value.Split(',');
    }

    /// <summary>Renders each search call as <c>shard Type?_id=ids</c>, in start order.</summary>
    public static string[] Searches(this FakeShardClient client) =>
        [.. client.Calls.Where(c => c.Search is not null).Select(c => $"{c.Shard} {c.Search!.Type.Value}?_id={string.Join(",", RequestedIds(c.Search))}")];

    /// <summary>Renders the includes as <c>shard/Type/id@version</c>, in emission order.</summary>
    public static string[] Rendered(this IncludeResult result) =>
        [.. result.Includes.Select(e => $"{e.Key.Shard}/{e.Key.Type.Value}/{e.Key.Id}@{e.VersionId}")];

    /// <summary>A successor cursor of <paramref name="request"/>, redeemed the way the stream that sent it requires.</summary>
    public static ShardCursor Cursor(ShardSearchRequest request, BackendProfile profile, string target) =>
        ShardCursor.Next(request, profile, $"https://{request.Shard}.example/fhir/{request.Type.Value}?ct={target}");

    public static ShardPage Page(ShardCursor? next, params ShardEntry[] entries) => new(entries, next, null);
}

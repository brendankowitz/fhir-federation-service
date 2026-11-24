using System.Collections.Frozen;
using System.Reflection;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// Two shards that certify <c>Observation.subject:Patient</c> reverse include searches differently: east (POST, up to 1000)
/// spells its own keys relative and canonical and foreign keys canonical; west (GET only, up to 500) spells its own keys
/// canonical and foreign keys canonical plus link base aliases, and owns a link base alias.
/// </summary>
internal static class RevIncludeScenario
{
    public const string RevInclude = "_revinclude=Observation:subject";

    public static readonly ShardId East = ShardId.Parse("east");
    public static readonly ShardId West = ShardId.Parse("west");
    public static readonly ResourceType Patient = TestSearch.Type("Patient");
    public static readonly ResourceType Observation = TestSearch.Type("Observation");

    public static readonly BackendProfile GetOnly = new("RevGetOnly", SupportsPostSearch: false, MaxCount: 500, NextLinkRedemption.FollowGet, null);

    public static ShardRegistry Registry() =>
        ShardRegistry.Create(
            [TestShards.East(), TestShards.West("https://west-legacy.example/fhir").WithProfile(GetOnly.Name)],
            [.. TestShards.Profiles, GetOnly]);

    /// <summary>
    /// Certifies only the reverse include search of <paramref name="path"/>, with the given forms, and its standalone shape
    /// unless <paramref name="withShape"/> is <see langword="false"/> (a certificate may prove the operation without it).
    /// </summary>
    public static ReferencePathCertification Certify(
        ReferencePath path,
        ReferenceSearchForms own,
        ReferenceSearchForms foreign,
        string registryVersion,
        ReferencePathOperations operations = ReferencePathOperations.RevIncludeSearch,
        bool withShape = true) => new(
        path,
        operations,
        own,
        foreign,
        registryVersion,
        null,
        withShape && operations.HasFlag(ReferencePathOperations.RevIncludeSearch) ? TestSearch.StandaloneShape(path) : null);

    public static ReferencePathCertification EastCertificate(ShardRegistry registry) => Certify(
        TestSearch.ObservationSubjectPatient,
        ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute,
        ReferenceSearchForms.CanonicalAbsolute,
        registry.Version);

    public static ReferencePathCertification WestCertificate(ShardRegistry registry) => Certify(
        TestSearch.ObservationSubjectPatient,
        ReferenceSearchForms.CanonicalAbsolute,
        ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases,
        registry.Version);

    /// <summary>Plans <paramref name="encoded"/> on Patient with each shard certifying as given.</summary>
    public static QueryPlan Plan(ShardRegistry registry, ReferencePathCertification east, ReferencePathCertification west, string encoded = RevInclude)
    {
        static SearchCertification Of(ReferencePathCertification certified) => TestSearch.Certification(paths: [certified]);
        var capabilities = new SearchCapabilities(
            registry,
            [
                new ShardSearchCapability(East, TestSearch.AllResources, Of(east), "4.0.1"),
                new ShardSearchCapability(West, TestSearch.AllResources, Of(west), "4.0.1"),
            ], TestSearch.R4Specification);
        return TestSearch.PlanOk("Patient", encoded, TestSearch.Planner(capabilities));
    }

    public static QueryPlan Plan(ShardRegistry? registry = null, string encoded = RevInclude)
    {
        registry ??= Registry();
        return Plan(registry, EastCertificate(registry), WestCertificate(registry), encoded);
    }

    /// <summary>Gets <paramref name="plan"/>'s reverse include.</summary>
    public static PlannedOccurrence RevIncludeOf(this QueryPlan plan) => plan.Occurrences.Single(o => o.Kind == OccurrenceKind.RevInclude);

    /// <summary>
    /// Replaces the certifications behind <paramref name="plan"/>'s reverse include and returns the plan with its replaced
    /// occurrence. The planner never admits a reverse include that any shard fails to prove under the current registry, and
    /// the Core assembly shows its internals to no test project, so the product's own constructors are called by reflection
    /// to reach the compiler's guard against evidence that does not match.
    /// </summary>
    public static (QueryPlan Plan, PlannedOccurrence Occurrence) Recertified(
        QueryPlan plan,
        ShardRegistry registry,
        IReadOnlyDictionary<ShardId, ReferencePathCertification> certifications)
    {
        PlannedOccurrence original = plan.RevIncludeOf();
        ReferenceHop hop = original.Hop!;
        var replaced = Construct<PlannedOccurrence>(
            original.Occurrence,
            original.Kind,
            original.Action,
            Construct<ReferenceHop>(hop.Path, hop.Definition, hop.Expression, hop.Mode, hop.Terminal, certifications.ToFrozenDictionary()),
            original.Specification);
        var recertified = Construct<QueryPlan>(
            plan.Type,
            plan.Query,
            plan.Occurrences.Select(o => ReferenceEquals(o, original) ? replaced : o).ToList(),
            plan.PageSize,
            plan.Sort,
            plan.IncludeTotal,
            plan.CountShape,
            registry,
            plan.Specification);
        return (recertified, replaced);
    }

    private static T Construct<T>(params object?[] arguments) =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)!;

    public static ResourceKey Key(ShardId shard, string id, ResourceType? type = null) => new(shard, type ?? Patient, ResourceId.Parse(id));

    /// <summary>Percent-decodes the request's single reference pair, as a backend reads it.</summary>
    public static string Decoded(this ShardSearchRequest request) => Uri.UnescapeDataString(request.Parameters.Single(p => p.Key == "subject").Value);

    /// <summary>
    /// Gets the bytes a transport sends for <paramref name="request"/> to its shard in <paramref name="registry"/>: the pairs
    /// and then the count, as a POST form body, or after the shard's base path and <c>/Type?</c> as a GET target.
    /// </summary>
    public static int WireBytes(this ShardSearchRequest request, ShardRegistry registry)
    {
        string query = $"{QueryRewriter.Render(request.Parameters)}&_count={request.PageSize}";
        string wire = request.Method switch
        {
            ShardSearchMethod.PostSearch => query,
            ShardSearchMethod.Get => $"{registry.Shards.Single(s => s.Id == request.Shard).BaseUrl.AbsolutePath.TrimEnd('/')}/{request.Type}?{query}",
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        return Encoding.UTF8.GetByteCount(wire);
    }
}

using System.Text;
using System.Text.Json;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// The collision corpus (two shards that both hold <c>Patient/dup</c>) and the configuration of a host that federates
/// it. East can take <c>POST _search</c>; west cannot.
/// </summary>
internal static class SearchWorld
{
    public const string PublicBase = "https://fhir.example.test/federation";
    public const string ReaderRole = "Federation.Reader";
    public const string ObservationRole = "Observation.Reader";
    public const string CallerObjectId = "caller-oid";
    public const string EastDupUrl = "https://fhir-east.example.org/Patient/dup";
    public const string WestDupUrl = "https://fhir-west.example.org/Patient/dup";

    public static readonly BackendProfile PostCapable = new("post-capable", SupportsPostSearch: true, 100, NextLinkRedemption.FollowGet, null);

    public static readonly BackendProfile GetOnly = new("get-only", SupportsPostSearch: false, 100, NextLinkRedemption.FollowGet, null);

    private static readonly Lazy<Corpus> Seeds = new(LoadCorpus);

    public static ShardRegistry Registry { get; } = ShardRegistry.Create(ShardOptions(), [PostCapable, GetOnly]);

    /// <summary>Gets each shard's seed resources in file order, as exact raw UTF-8 slices.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<SeedResource>> Resources => Seeds.Value.Resources;

    public static byte[] Slice(string shard, string type, string id) =>
        Resources[shard].Single(r => r.Type == type && r.Id == id).Json;

    public static IReadOnlyDictionary<string, string> ExpectedGiven => Seeds.Value.ExpectedGiven;

    /// <summary>A fresh, mutable copy of a valid configured host's settings.</summary>
    public static Dictionary<string, string> Settings(bool extraWitnessShard = false)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Federation:Host:PublicBaseUrl"] = PublicBase,
            ["Federation:Host:Authority"] = SyntheticIdentityProvider.Authority.OriginalString,
            ["Federation:Host:Issuer"] = SyntheticIdentityProvider.TrustedIssuer,
            ["Federation:Host:Audience"] = SyntheticIdentityProvider.TrustedAudience,
            [$"Federation:Host:Roles:{ReaderRole}:0"] = "Patient",
            [$"Federation:Host:Roles:{ReaderRole}:1"] = "Observation",
            [$"Federation:Host:Roles:{ObservationRole}:0"] = "Observation",
        };

        List<ShardOptions> shards = ShardOptions(extraWitnessShard);
        for (int i = 0; i < shards.Count; i++)
        {
            string prefix = $"Federation:Shards:{i}";
            settings[$"{prefix}:Id"] = shards[i].Id!;
            settings[$"{prefix}:Alias"] = shards[i].Alias!;
            settings[$"{prefix}:BaseUrl"] = shards[i].BaseUrl!;
            settings[$"{prefix}:Profile"] = shards[i].Profile!;
            settings[$"{prefix}:Auth:Type"] = shards[i].Auth!.Type!;
            settings[$"{prefix}:Auth:Resource"] = shards[i].Auth!.Resource!;
        }

        return settings;
    }

    /// <summary>Both shards search <c>Patient</c> by <c>name</c>, <c>family</c> and <c>_id</c>, and <c>Observation</c> by <c>code</c>.</summary>
    /// <param name="patientName">Whether <c>Patient.name</c> is searchable.</param>
    /// <param name="exactCountShapes">The shapes every shard counts exactly; by default only the bare <c>Patient?</c>.</param>
    /// <param name="organizationMissing">
    /// Whether <c>Patient.organization:missing</c> is searchable. The planner admits it, but the published
    /// <see cref="SearchCapabilities.Resources"/> projection never lists a reference parameter.
    /// </param>
    /// <param name="patient">Whether <c>Patient</c> is searchable at all.</param>
    /// <param name="withoutExactCountProof">The shard with no exact-count proof, if any.</param>
    /// <param name="reverseChain">Whether to certify the fixture's Observation subject path.</param>
    /// <param name="registry">The test registry or the default two-shard registry.</param>
    /// <param name="forwardChain">Whether to certify <c>Observation.subject:Patient</c> for native (Passthrough) forward chains.</param>
    /// <param name="distributedForward">
    /// Whether to certify <c>Observation.subject:Patient</c> for distributed forward chains too, with the reference alternatives
    /// that make the planner admit such a chain.
    /// </param>
    /// <param name="organizationForward">
    /// Whether <c>Organization</c> is searchable by <c>name</c> and <c>Patient.organization:Organization</c> is certified for native
    /// forward chains.
    /// </param>
    /// <param name="revInclude">
    /// Whether to certify <c>Observation.subject:Patient</c> for <c>_revinclude</c> source searches under the registry's version and
    /// advertise <c>Patient</c>'s <c>searchRevInclude</c> <c>Observation:subject</c>.
    /// </param>
    /// <param name="organizationInclude">
    /// Whether <c>Patient.organization:Organization</c> is certified for <c>_include</c> extraction and advertised, so a
    /// true include is a valid but unexecuted request.
    /// </param>
    /// <param name="conditionRevInclude">
    /// Whether <c>Condition.subject:Patient</c> is a second <c>_revinclude</c> source, advertised on <c>Patient</c>, so a request can
    /// mix an authorized and a denied include type. Used together with <paramref name="revInclude"/>.
    /// </param>
    public static SearchCapabilities Capabilities(
        bool patientName = true,
        IEnumerable<string>? exactCountShapes = null,
        bool organizationMissing = false,
        bool patient = true,
        string? withoutExactCountProof = null,
        bool reverseChain = false,
        ShardRegistry? registry = null,
        bool forwardChain = false,
        bool distributedForward = false,
        bool organizationForward = false,
        bool revInclude = false,
        bool organizationInclude = false,
        bool conditionRevInclude = false)
    {
        registry ??= Registry;
        List<SearchParameterSupport> patientParameters =
        [
            new("_id", SearchParamType.Token, null),
            new("family", SearchParamType.String, null),
        ];
        if (patientName)
        {
            patientParameters.Add(new("name", SearchParamType.String, null));
        }

        if (organizationMissing || organizationForward || organizationInclude)
        {
            patientParameters.Add(new("organization", SearchParamType.Reference, null));
        }

        List<SearchParameterSupport> observationParameters = [new("_id", SearchParamType.Token, null), new("code", SearchParamType.Token, null)];
        List<ReferencePathCertification> paths = [];
        ReferencePathOperations subject = ReferencePathOperations.None;
        if (reverseChain)
        {
            subject |= ReferencePathOperations.DistributedReverse | ReferencePathOperations.NativeReverse;
        }

        if (forwardChain)
        {
            subject |= ReferencePathOperations.NativeForward;
        }

        if (distributedForward)
        {
            subject |= ReferencePathOperations.DistributedForward;
        }

        if (revInclude)
        {
            subject |= ReferencePathOperations.RevIncludeSearch;
        }

        if (subject != ReferencePathOperations.None)
        {
            observationParameters.Add(new("subject", SearchParamType.Reference, null));
            ReferencePath subjectPath = new(ResourceType.Parse("Observation"), "subject", ResourceType.Parse("Patient"));
            paths.Add(distributedForward || revInclude
                ? new(subjectPath, subject, ReferenceSearchForms.Relative, ReferenceSearchForms.CanonicalAbsolute, registry.Version,
                    distributedForward ? ["Observation?@Observation.subject:Patient"] : null,
                    revInclude ? "Observation?@Observation.subject:Patient" : null)
                : new(subjectPath, subject, ReferenceSearchForms.None, ReferenceSearchForms.None));
        }

        List<ResourceSearchSupport> resources =
        [
            new(ResourceType.Parse("Observation"), observationParameters, [], []),
        ];
        if (patient)
        {
            List<string> revIncludes = [];
            if (revInclude)
            {
                revIncludes.Add("Observation:subject");
            }

            if (conditionRevInclude)
            {
                revIncludes.Add("Condition:subject");
            }

            resources.Insert(0, new(ResourceType.Parse("Patient"), patientParameters, organizationInclude ? ["Patient:organization"] : [], revIncludes));
        }

        if (conditionRevInclude)
        {
            resources.Add(new(ResourceType.Parse("Condition"),
                [new("_id", SearchParamType.Token, null), new("subject", SearchParamType.Reference, null)], [], []));
            paths.Add(new(new ReferencePath(ResourceType.Parse("Condition"), "subject", ResourceType.Parse("Patient")),
                ReferencePathOperations.RevIncludeSearch, ReferenceSearchForms.Relative, ReferenceSearchForms.CanonicalAbsolute,
                registry.Version, null, "Condition?@Condition.subject:Patient"));
        }

        if (organizationForward || organizationInclude)
        {
            List<SearchParameterSupport> organizationParameters = [new("name", SearchParamType.String, null)];
            if (organizationInclude)
            {
                organizationParameters.Add(new("_id", SearchParamType.Token, null));
            }

            resources.Add(new(ResourceType.Parse("Organization"), organizationParameters, [], []));
            ReferencePathOperations organization = (organizationForward ? ReferencePathOperations.NativeForward : ReferencePathOperations.None)
                | (organizationInclude ? ReferencePathOperations.IncludeExtraction : ReferencePathOperations.None);
            paths.Add(new(new ReferencePath(ResourceType.Parse("Patient"), "organization", ResourceType.Parse("Organization")),
                organization, ReferenceSearchForms.None, ReferenceSearchForms.None));
        }

        SearchCertification Certify(IEnumerable<string> countShapes) => new(
            new Dictionary<SearchParamType, IReadOnlyCollection<string>> { [SearchParamType.String] = ["exact"], [SearchParamType.Reference] = ["missing"] },
            [SearchParamType.String, SearchParamType.Token],
            distributedForward || revInclude || conditionRevInclude ? [SearchParamType.String, SearchParamType.Token, SearchParamType.Reference] : [SearchParamType.String, SearchParamType.Token],
            countShapes,
            paths);

        return new SearchCapabilities(
            registry,
            registry.Shards.Select(s => new ShardSearchCapability(s.Id, resources,
                Certify(s.Id.Value == withoutExactCountProof ? [] : exactCountShapes ?? ["Patient?"]))));
    }

    public static ShardRegistry ReverseRegistry(bool extraWitnessShard, BackendProfile? east = null) =>
        ShardRegistry.Create(ShardOptions(extraWitnessShard), [east ?? PostCapable, GetOnly]);

    private static List<ShardOptions> ShardOptions(bool extraWitnessShard = false)
    {
        List<ShardOptions> shards =
        [
            Shard("east", "k7q2", "https://fhir-east.example.org/", PostCapable),
            Shard("west", "p9xm", "https://fhir-west.example.org/", GetOnly),
        ];
        if (extraWitnessShard)
        {
            shards.Add(Shard("north", "n5w2", "https://fhir-north.example.org/", GetOnly));
        }

        return shards;
    }

    private static ShardOptions Shard(string id, string alias, string baseUrl, BackendProfile profile) => new()
    {
        Id = id,
        Alias = alias,
        BaseUrl = baseUrl,
        Profile = profile.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.example" },
    };

    private static Corpus LoadCorpus()
    {
        string root = LocateOracleRoot();
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        JsonElement collision = manifest.RootElement.GetProperty("corpora").GetProperty("collision");

        var resources = new Dictionary<string, IReadOnlyList<SeedResource>>(StringComparer.Ordinal);
        foreach (JsonProperty placement in collision.GetProperty("placements").GetProperty("per-shard").EnumerateObject())
        {
            byte[] file = File.ReadAllBytes(Path.Combine(root, placement.Value.GetString()!));
            using JsonDocument seed = JsonDocument.Parse(file);
            resources[placement.Name] =
            [
                .. seed.RootElement.EnumerateArray().Select(r => new SeedResource(
                    r.GetProperty("resourceType").GetString()!,
                    r.GetProperty("id").GetString()!,
                    Encoding.UTF8.GetBytes(r.GetRawText()))),
            ];
        }

        Dictionary<string, string> expectedGiven = collision.GetProperty("expectedGiven")
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value[0].GetString()!, StringComparer.Ordinal);
        return new Corpus(resources, expectedGiven);
    }

    private static string LocateOracleRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "test", "fixtures", "federation-oracle");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"test/fixtures/federation-oracle/manifest.json was not found above {AppContext.BaseDirectory}.");
    }

    internal sealed record SeedResource(string Type, string Id, byte[] Json);

    private sealed record Corpus(
        IReadOnlyDictionary<string, IReadOnlyList<SeedResource>> Resources,
        IReadOnlyDictionary<string, string> ExpectedGiven);
}

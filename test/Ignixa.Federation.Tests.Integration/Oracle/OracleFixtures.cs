using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.Integration.Oracle;

internal enum OracleCorpus
{
    UniqueId,
    Collision,
}

/// <summary>
/// The one spelling rule shared by registered bases and observed fullUrls, so that two spellings of one physical
/// URL are one identity: lower-case scheme and host, the port only when it is not the scheme default, and everything
/// after the authority kept exactly as written (case-sensitive, never unescaped). DNS aliases are out of scope.
/// </summary>
internal static class OracleUrl
{
    /// <returns>
    /// The canonical spelling, or null unless <paramref name="url"/> is an absolute http(s) URL with a host and no
    /// userinfo. Query and fragment are kept as written; callers that forbid them check for them.
    /// </returns>
    public static string? Canonicalize(string url)
    {
        // Whitespace and backslashes are rejected because System.Uri silently trims or rewrites them, which would
        // let the raw text after the authority disagree with what Uri parsed as the authority.
        if (url.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\')
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !url.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var authorityStart = uri.Scheme.Length + "://".Length;
        var authorityEnd = url.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];
        if (authority.Length == 0 || authority.Contains('@') || uri.Host.Length == 0)
        {
            return null;
        }

        var rest = authorityEnd < 0 ? string.Empty : url[authorityEnd..];
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + (rest.StartsWith('/') ? rest : "/" + rest);
    }
}

/// <summary>
/// The bases a harness run actually registered: shard id to base URL, plus the unique-id oracle server's base.
/// Every base is stored in <see cref="OracleUrl"/> canonical form, ending with exactly one '/'.
/// </summary>
internal sealed record OracleBases
{
    /// <exception cref="ArgumentException">A base is not an absolute http(s) URL with a host and no userinfo, query, fragment, percent-escape or dot segment.</exception>
    public OracleBases(IReadOnlyDictionary<string, string> shards, string? oracle)
    {
        Shards = shards.ToDictionary(shard => shard.Key, shard => Canonical($"shard '{shard.Key}'", shard.Value, nameof(shards)), StringComparer.Ordinal);
        Oracle = oracle is null ? null : Canonical("oracle", oracle, nameof(oracle));
    }

    public IReadOnlyDictionary<string, string> Shards { get; }

    public string? Oracle { get; }

    private static string Canonical(string registration, string baseUrl, string paramName)
    {
        var canonical = OracleUrl.Canonicalize(baseUrl);
        if (canonical is null
            || canonical.IndexOfAny(['?', '#']) >= 0
            || baseUrl.Contains('%')
            || HasDotSegment(canonical))
        {
            throw new ArgumentException(
                $"The {registration} base '{baseUrl}' must be an absolute http(s) URL with a host and no userinfo, query, fragment, percent-escape or dot segment.",
                paramName);
        }

        return canonical.TrimEnd('/') + "/";
    }

    private static bool HasDotSegment(string canonicalBase)
    {
        var authorityStart = canonicalBase.IndexOf("://", StringComparison.Ordinal) + "://".Length;
        var pathStart = canonicalBase.IndexOf('/', authorityStart);
        return pathStart >= 0 && canonicalBase[pathStart..].Split('/').Any(segment => segment is "." or "..");
    }
}

/// <summary>A registered base and the <c>Type/id</c>s the chosen placement seeded behind it.</summary>
/// <param name="Owner">The manifest shard id, or <c>oracle</c> for the unique-id oracle server.</param>
internal sealed record SeededBase(string Owner, string BaseUrl, IReadOnlySet<string> Seeded);

/// <summary>
/// One manifest case bound to a run's real bases. Identities are <c>Type/id</c> for the unique-id corpus and
/// owning fullUrls, already rebased onto the registered shard bases, for the collision corpus.
/// </summary>
/// <param name="ExpectedOracleMatches">Unique-id only: <c>expectedMatches</c> plus <c>missedVsOracle</c>; null for collision.</param>
/// <param name="OracleBase">Unique-id only; null for collision, which never consults the oracle.</param>
internal sealed record BoundCase(
    string Id,
    OracleCorpus Corpus,
    bool IsNegativeControl,
    int? Count,
    IReadOnlyList<string> ExpectedFederationMatches,
    IReadOnlyList<string>? ExpectedOracleMatches,
    IReadOnlyList<string> MissedVsOracle,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IncludeRelations,
    IReadOnlyList<string> CoEmitted,
    IReadOnlyList<string> WitnessIncludes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedGiven,
    IReadOnlyList<SeededBase> ShardBases,
    SeededBase? OracleBase);

/// <summary>
/// Reads the committed federation-oracle manifest and seeds and binds one case to a run's registered bases.
/// Misconfigured runs (unknown case, disallowed placement, bad bases) are programmer errors and throw
/// <see cref="ArgumentException"/>; a malformed manifest throws <see cref="InvalidDataException"/>.
/// </summary>
internal static class OracleFixtures
{
    private const string OracleOwner = "oracle";

    public static BoundCase Bind(string caseId, string placement, OracleBases bases)
    {
        var manifestPath = LocateManifest();
        var fixtureDirectory = Path.GetDirectoryName(manifestPath)!;
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException($"{manifestPath} is empty.");

        var testCase = Required(manifest, "cases").AsArray()
            .Select(node => node!.AsObject())
            .SingleOrDefault(node => (string?)node["id"] == caseId)
            ?? throw new ArgumentException($"Unknown oracle case '{caseId}'.", nameof(caseId));

        var allowedPlacements = testCase["placements"] is JsonArray placements ? Strings(placements) : [RequiredString(testCase, "placement")];
        if (!allowedPlacements.Contains(placement))
        {
            throw new ArgumentException(
                $"Placement '{placement}' is not allowed for case '{caseId}' (allowed: {string.Join(", ", allowedPlacements)}).",
                nameof(placement));
        }

        var corpusName = RequiredString(testCase, "corpus");
        var corpus = corpusName switch
        {
            "unique-id" => OracleCorpus.UniqueId,
            "collision" => OracleCorpus.Collision,
            _ => throw new InvalidDataException($"Case '{caseId}' has unknown corpus '{corpusName}'."),
        };

        var reservedBases = Required(manifest, "shards").AsArray()
            .ToDictionary(shard => RequiredString(shard!, "id"), shard => RequiredString(shard!, "baseUrl"), StringComparer.Ordinal);
        ValidateBases(reservedBases.Keys, corpus, bases);

        var corpusNode = Required(Required(manifest, "corpora"), corpusName);
        var seedFiles = Required(Required(corpusNode, "placements"), placement);
        var shardBases = reservedBases.Keys
            .Select(shard => new SeededBase(shard, bases.Shards[shard], LoadSeeded(fixtureDirectory, RequiredString(seedFiles, shard))))
            .ToList();
        var oracleBase = corpus == OracleCorpus.UniqueId
            ? new SeededBase(OracleOwner, bases.Oracle!, LoadSeeded(fixtureDirectory, RequiredString(corpusNode, "oracle")))
            : null;

        // Collision answers are owning fullUrls under the reserved bases; unique-id answers are Type/id and stay as they are.
        Func<string, string> answer = corpus == OracleCorpus.Collision ? url => Rebase(url, reservedBases, bases) : identity => identity;
        IReadOnlyList<string> Answers(string name) => [.. StringsOrEmpty(testCase[name]).Select(answer)];

        var expectedFederation = Answers(corpus == OracleCorpus.UniqueId ? "expectedMatches" : "expectedFullUrls");
        var missedVsOracle = Answers("missedVsOracle");

        return new BoundCase(
            Id: caseId,
            Corpus: corpus,
            IsNegativeControl: (string?)testCase["role"] == "negative-control",
            Count: (int?)testCase["count"],
            ExpectedFederationMatches: expectedFederation,
            ExpectedOracleMatches: corpus == OracleCorpus.UniqueId ? [.. expectedFederation, .. missedVsOracle] : null,
            MissedVsOracle: missedVsOracle,
            IncludeRelations: StringLists(testCase["includeRelations"], answer),
            CoEmitted: Answers("coEmitted"),
            WitnessIncludes: Answers("witnessIncludes"),
            ExpectedGiven: corpus == OracleCorpus.Collision ? StringLists(corpusNode["expectedGiven"], answer, givenName => givenName) : [],
            ShardBases: shardBases,
            OracleBase: oracleBase);
    }

    private static string LocateManifest()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "test", "fixtures", "federation-oracle", "manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"test/fixtures/federation-oracle/manifest.json was not found above {AppContext.BaseDirectory}.");
    }

    private static void ValidateBases(IEnumerable<string> manifestShards, OracleCorpus corpus, OracleBases bases)
    {
        var expected = manifestShards.ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(shard => !bases.Shards.ContainsKey(shard)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Manifest shard(s) {string.Join(", ", missing)} have no registered base.", nameof(bases));
        }

        var unknown = bases.Shards.Keys.Where(shard => !expected.Contains(shard)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Registered shard(s) {string.Join(", ", unknown)} are not in the manifest.", nameof(bases));
        }

        // Bases are canonical (OracleBases), so ordinal equality here is spelling-insensitive physical equality.
        var registrations = bases.Shards.Select(shard => (Owner: shard.Key, BaseUrl: shard.Value));
        if (bases.Oracle is not null)
        {
            registrations = registrations.Append((OracleOwner, bases.Oracle));
        }

        var duplicates = registrations
            .GroupBy(registration => registration.BaseUrl, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(registration => registration.Owner))})")
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException($"Each registered base must be distinct; duplicated: {string.Join(", ", duplicates)}.", nameof(bases));
        }

        if (corpus == OracleCorpus.UniqueId && bases.Oracle is null)
        {
            throw new ArgumentException("Unique-id cases need the oracle server's base.", nameof(bases));
        }
    }

    private static string Rebase(string url, IReadOnlyDictionary<string, string> reservedBases, OracleBases bases)
    {
        foreach (var (shard, reserved) in reservedBases)
        {
            if (url.StartsWith(reserved, StringComparison.Ordinal))
            {
                return bases.Shards[shard] + url[reserved.Length..];
            }
        }

        throw new InvalidDataException($"Collision answer '{url}' is not under a reserved manifest shard base.");
    }

    private static HashSet<string> LoadSeeded(string fixtureDirectory, string relativePath)
    {
        var path = Path.Combine(fixtureDirectory, relativePath);
        var resources = JsonNode.Parse(File.ReadAllText(path))?.AsArray()
            ?? throw new InvalidDataException($"{path} is empty.");
        return resources
            .Select(resource => $"{RequiredString(resource!, "resourceType")}/{RequiredString(resource!, "id")}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<string>> StringLists(
        JsonNode? node, Func<string, string> mapKey, Func<string, string>? mapValue = null)
    {
        mapValue ??= mapKey;
        return node is null
            ? []
            : node.AsObject().ToDictionary(
                pair => mapKey(pair.Key),
                pair => (IReadOnlyList<string>)[.. Strings(pair.Value!.AsArray()).Select(mapValue)],
                StringComparer.Ordinal);
    }

    private static List<string> StringsOrEmpty(JsonNode? node) => node is null ? [] : Strings(node.AsArray());

    private static List<string> Strings(JsonArray array) => [.. array.Select(item => item!.GetValue<string>())];

    private static JsonNode Required(JsonNode node, string name) =>
        node[name] ?? throw new InvalidDataException($"Manifest node {node.GetPath()} is missing '{name}'.");

    private static string RequiredString(JsonNode node, string name) => Required(node, name).GetValue<string>();
}

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The validated, immutable set of shards (design spec §4.2): lookups, the registry version and full URLs. Safe to
/// share across threads.
/// </summary>
public sealed class ShardRegistry
{
    private const string ShardsPath = "Federation:Shards";

    private readonly Dictionary<ShardId, ShardDescriptor> _byId;
    private readonly Dictionary<ShardAlias, ShardDescriptor> _byAlias;

    private ShardRegistry(ShardDescriptor[] shards)
    {
        Shards = Array.AsReadOnly(shards);
        _byId = shards.ToDictionary(s => s.Id);
        _byAlias = shards.ToDictionary(s => s.Alias);
        Version = Digest(shards);
    }

    /// <summary>Gets the shards in ordinal order; the ordinal is the zero-based position in configuration.</summary>
    public IReadOnlyList<ShardDescriptor> Shards { get; }

    /// <summary>
    /// Gets the registry version: 64 lowercase hex characters of a SHA-256 over every shard's id, alias, canonical
    /// base URL, canonical link aliases, profile name and ordinal (design spec §4.2). Auth is excluded, so rotating a
    /// credential does not invalidate continuation tokens. The encoding is a compatibility contract.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Validates configured shards and builds a registry. Shard count above the soft limit is not an error here.
    /// </summary>
    /// <param name="shards">The configured shards, in ordinal order. Copied.</param>
    /// <param name="profiles">The backend profiles a shard may name, with unique names.</param>
    /// <param name="registry">The registry, when the configuration is valid.</param>
    /// <param name="errors">
    /// Every problem, each prefixed with its configuration path (for example <c>Federation:Shards:1:Alias: ...</c>);
    /// empty on success. Auth values are never echoed.
    /// </param>
    /// <returns><see langword="true"/> when the configuration is valid.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shards"/> or <paramref name="profiles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="profiles"/> holds a <see langword="null"/> or a repeated name.</exception>
    public static bool TryCreate(
        IEnumerable<ShardOptions> shards,
        IEnumerable<BackendProfile> profiles,
        [NotNullWhen(true)] out ShardRegistry? registry,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(shards);
        ArgumentNullException.ThrowIfNull(profiles);

        Dictionary<string, BackendProfile> profilesByName = IndexProfiles(profiles);
        ShardOptions?[] input = [.. shards];
        var found = new List<string>();
        if (input.Length == 0)
        {
            found.Add($"{ShardsPath}: at least one shard is required.");
        }

        var ids = new Dictionary<ShardId, int>();
        var aliases = new Dictionary<ShardAlias, int>();
        var claimedBases = new List<ClaimedBase>();
        var descriptors = new ShardDescriptor[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            string path = $"{ShardsPath}:{i}";
            if (input[i] is not { } options)
            {
                found.Add($"{path}: must not be null.");
                continue;
            }

            ShardId? id = ParseUnique(options.Id, ShardId.TryParse, ShardId.Validate, ids, i, "Id", found);
            ShardAlias? alias = ParseUnique(options.Alias, ShardAlias.TryParse, ShardAlias.Validate, aliases, i, "Alias", found);
            string? baseUrl = ClaimBase(options.BaseUrl, i, $"{path}:BaseUrl", claimedBases, found);
            var linkBaseAliases = new List<string>(options.LinkBaseAliases.Count);
            for (int j = 0; j < options.LinkBaseAliases.Count; j++)
            {
                if (ClaimBase(options.LinkBaseAliases[j], i, $"{path}:LinkBaseAliases:{j}", claimedBases, found) is { } linkBaseAlias)
                {
                    linkBaseAliases.Add(linkBaseAlias);
                }
            }

            if (options.Profile is null || !profilesByName.TryGetValue(options.Profile, out BackendProfile? profile))
            {
                found.Add($"{path}:Profile: must name a supplied backend profile.");
                profile = null;
            }

            ShardAuth? auth = ShardAuth.Parse(options.Auth, $"{path}:Auth", found);
            if (id is not null && alias is not null && baseUrl is not null && profile is not null && auth is not null)
            {
                descriptors[i] = new ShardDescriptor(id, alias, baseUrl, linkBaseAliases.AsReadOnly(), profile, auth, i);
            }
        }

        registry = found.Count == 0 ? new ShardRegistry(descriptors) : null;
        errors = found.AsReadOnly();
        return registry is not null;
    }

    /// <summary>Validates configured shards and builds a registry.</summary>
    /// <param name="shards">The configured shards, in ordinal order. Copied.</param>
    /// <param name="profiles">The backend profiles a shard may name, with unique names.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shards"/> or <paramref name="profiles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The configuration is invalid (the message lists every error), or <paramref name="profiles"/> holds a
    /// <see langword="null"/> or a repeated name.
    /// </exception>
    public static ShardRegistry Create(IEnumerable<ShardOptions> shards, IEnumerable<BackendProfile> profiles)
    {
        return TryCreate(shards, profiles, out ShardRegistry? registry, out IReadOnlyList<string> errors)
            ? registry
            : throw new ArgumentException(
                $"Invalid shard configuration:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}",
                nameof(shards));
    }

    /// <summary>Finds a shard by its identifier (ordinal, case-sensitive).</summary>
    /// <param name="id">The shard identifier.</param>
    /// <param name="shard">The shard, when found.</param>
    /// <returns><see langword="true"/> when the registry holds the shard.</returns>
    public bool TryGetById(ShardId id, [NotNullWhen(true)] out ShardDescriptor? shard) => _byId.TryGetValue(id, out shard);

    /// <summary>Finds a shard by its alias.</summary>
    /// <param name="alias">The shard alias.</param>
    /// <param name="shard">The shard, when found.</param>
    /// <returns><see langword="true"/> when the registry holds the shard.</returns>
    public bool TryGetByAlias(ShardAlias alias, [NotNullWhen(true)] out ShardDescriptor? shard) => _byAlias.TryGetValue(alias, out shard);

    /// <summary>
    /// Computes the version of a subset of shards, for continuation tokens that touch only some shards: the
    /// <see cref="Version"/> encoding over the participating shards in ordinal order, keeping their original ordinals.
    /// A change to a shard outside the subset does not change it.
    /// </summary>
    /// <param name="participating">The participating shards; order and repeats do not matter.</param>
    /// <returns>64 lowercase hex characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="participating"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="participating"/> holds a <see langword="null"/> or an unknown shard.</exception>
    public string ComputeVersion(IEnumerable<ShardId> participating)
    {
        ArgumentNullException.ThrowIfNull(participating);

        var ids = new HashSet<ShardId>();
        foreach (ShardId id in participating)
        {
            if (id is null || !_byId.ContainsKey(id))
            {
                throw new ArgumentException("Every participating shard must be in the registry.", nameof(participating));
            }

            ids.Add(id);
        }

        return Digest([.. Shards.Where(s => ids.Contains(s.Id))]);
    }

    /// <summary>
    /// Builds the full URL of a resource: <c>{canonical base URL}/{type}/{id}</c> by concatenation, so an id such as
    /// <c>..</c> or a base path is kept verbatim. The same type and id on two shards give two URLs.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The full URL.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The key's shard is not in the registry.</exception>
    [SuppressMessage(
        "Design",
        "CA1055:URI-like return values should not be strings",
        Justification = "A Uri would re-normalise '.' and '..' ids; the full URL is the exact concatenated text.")]
    public string FullUrl(ResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _byId.TryGetValue(key.Shard, out ShardDescriptor? shard)
            ? $"{shard.CanonicalBaseUrl}/{key.Type.Value}/{key.Id.Value}"
            : throw new ArgumentException("The key's shard is not in the registry.", nameof(key));
    }

    private static Dictionary<string, BackendProfile> IndexProfiles(IEnumerable<BackendProfile> profiles)
    {
        var byName = new Dictionary<string, BackendProfile>(StringComparer.Ordinal);
        foreach (BackendProfile profile in profiles)
        {
            if (profile is null || !byName.TryAdd(profile.Name, profile))
            {
                throw new ArgumentException("Backend profiles must be non-null with unique names.", nameof(profiles));
            }
        }

        return byName;
    }

    private static T? ParseUnique<T>(
        string? value,
        TryParseFunc<T> tryParse,
        Func<string?, string?> validate,
        Dictionary<T, int> seen,
        int shard,
        string field,
        List<string> errors)
        where T : class
    {
        string path = $"{ShardsPath}:{shard}:{field}";
        if (!tryParse(value, out T? parsed))
        {
            errors.Add($"{path}: {validate(value)}");
            return null;
        }

        if (!seen.TryAdd(parsed, shard))
        {
            errors.Add($"{path}: duplicates {ShardsPath}:{seen[parsed]}:{field}.");
            return null;
        }

        return parsed;
    }

    /// <summary>
    /// Canonicalises a base form and claims it for <paramref name="shard"/>. A form may not equal any claimed form, and
    /// may not be nested (on a segment boundary) over or under another shard's form.
    /// </summary>
    private static string? ClaimBase(string? value, int shard, string path, List<ClaimedBase> claimed, List<string> errors)
    {
        if (ShardBaseUrl.Canonicalize(value, out string canonical) is { } error)
        {
            errors.Add($"{path}: {error}");
            return null;
        }

        foreach (ClaimedBase other in claimed)
        {
            if (string.Equals(canonical, other.Canonical, StringComparison.Ordinal))
            {
                errors.Add($"{path}: duplicates {other.Path}.");
                return null;
            }

            if (shard != other.Shard
                && (canonical.StartsWith(other.Canonical + "/", StringComparison.Ordinal)
                    || other.Canonical.StartsWith(canonical + "/", StringComparison.Ordinal)))
            {
                errors.Add($"{path}: must not be nested over or under {other.Path} of another shard.");
                return null;
            }
        }

        claimed.Add(new ClaimedBase(canonical, shard, path));
        return canonical;
    }

    private static string Digest(ShardDescriptor[] shards)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("IFR1"u8);
        AppendUInt32(hash, shards.Length);
        foreach (ShardDescriptor shard in shards)
        {
            AppendString(hash, shard.Id.Value);
            AppendString(hash, shard.Alias.Value);
            AppendString(hash, shard.CanonicalBaseUrl);
            AppendUInt32(hash, shard.CanonicalLinkBaseAliases.Count);
            foreach (string linkBaseAlias in shard.CanonicalLinkBaseAliases.Order(StringComparer.Ordinal))
            {
                AppendString(hash, linkBaseAlias);
            }

            AppendString(hash, shard.Profile.Name);
            AppendUInt32(hash, shard.Ordinal);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendUInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value));
        hash.AppendData(bytes);
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        AppendUInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private delegate bool TryParseFunc<T>(string? value, [NotNullWhen(true)] out T? result);

    private readonly record struct ClaimedBase(string Canonical, int Shard, string Path);
}

using System.Globalization;
using System.Text.RegularExpressions;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Reads the settings the prototype backend consumes from a <c>Federation</c> configuration section into the inputs of
/// <see cref="PrototypeShardBackendExtensions"/>. This is the syntax boundary only: it checks the shape of the settings it
/// owns and parses text into numbers, and leaves every semantic rule to the existing registration.
/// </summary>
/// <remarks>
/// <para>Only Shards-owned keys are read. The section is inspected through explicit child lookups rather than a binder, so
/// unrelated keys are never touched and the <c>Backend</c> secret mount keys are never read unless a shard uses client
/// credentials. Every problem is reported as its configuration path and a fixed sentence: neither a submitted value nor a
/// provider-supplied key (other than a decimal index) is echoed, and there is no inner exception.</para>
/// <para>Numeric list indexes may have gaps. The registration identifies shards and link aliases by compact position, so
/// <see cref="Translate"/> maps its diagnostics back to the keys exactly as they were configured.</para>
/// </remarks>
internal sealed partial class PrototypeShardBackendSettings
{
    private const string FallbackPrefix = "Federation";

    private readonly string _prefix;
    private readonly List<string> _failures = [];
    private readonly List<string> _shardKeys = [];
    private readonly List<List<string>> _aliasKeys = [];

    private PrototypeShardBackendSettings(string prefix) => _prefix = prefix;

    private enum Shape
    {
        Absent,
        Group,
        Scalar,
        Invalid,
    }

    /// <summary>Gets the options the registration validates; the shards and the transport settings.</summary>
    public FederationOptions Options { get; } = new();

    /// <summary>Gets the per-shard wire ceilings, keyed by the ids that parse. Repeated or unparsable ids are left to the registry.</summary>
    public Dictionary<ShardId, ShardRequestLimits> WireLimits { get; } = [];

    /// <summary>Gets the secret mount root, or <see langword="null"/> when not read or not configured.</summary>
    public string? SecretMountRoot { get; private set; }

    /// <summary>Gets the secret name to leaf map in configured numeric order, or <see langword="null"/> when not read or not configured.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? SecretFiles { get; private set; }

    /// <summary>Reads the Shards-owned settings.</summary>
    /// <param name="section">The federation section.</param>
    /// <returns>The inputs for the registration.</returns>
    /// <exception cref="OptionsValidationException">A setting has the wrong shape or cannot be parsed.</exception>
    public static PrototypeShardBackendSettings Read(IConfigurationSection section)
    {
        var settings = new PrototypeShardBackendSettings(section.Path.Length > 0 ? section.Path : FallbackPrefix);
        settings.ReadShards(section.GetSection("Shards"));
        settings.ReadTransport(section);
        if (settings.Options.Shards.Any(shard => shard.Auth?.Type == "ClientCredentials"))
        {
            settings.ReadMount(section.GetSection("Backend"));
        }

        if (settings._failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(FederationOptions), typeof(FederationOptions), settings._failures);
        }

        return settings;
    }

    /// <summary>
    /// Rewrites the registration's diagnostics, which name <c>Federation:Shards:{position}</c>, the call's parameters and the
    /// default section name, to the configured setting names.
    /// </summary>
    /// <param name="failure">The registration's refusal.</param>
    /// <returns>A refusal with the same failures at the configured paths, and no inner exception.</returns>
    public OptionsValidationException Translate(OptionsValidationException failure)
    {
        IEnumerable<string> failures = failure.Failures.Select(text => OwnedPath().Replace(
            text.Replace("secretMountRoot", "Federation:Backend:SecretMountRoot", StringComparison.Ordinal)
                .Replace("secretFiles", "Federation:Backend:SecretFiles", StringComparison.Ordinal),
            ConfiguredPath));
        return new OptionsValidationException(nameof(FederationOptions), typeof(FederationOptions), failures);
    }

    [GeneratedRegex(@"Federation(?::Shards:(?<shard>\d+)(?::LinkBaseAliases:(?<alias>\d+))?)?", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedPath();

    private static bool TryIndex(string key, out int index)
    {
        index = 0;
        return key.Length > 0 && key.All(char.IsAsciiDigit) && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private string ConfiguredPath(Match match)
    {
        if (!match.Groups["shard"].Success)
        {
            return _prefix;
        }

        int position = int.Parse(match.Groups["shard"].Value, CultureInfo.InvariantCulture);
        if (position >= _shardKeys.Count)
        {
            return match.Value;
        }

        string path = $"{_prefix}:Shards:{_shardKeys[position]}";
        if (match.Groups["alias"].Success)
        {
            int alias = int.Parse(match.Groups["alias"].Value, CultureInfo.InvariantCulture);
            if (alias < _aliasKeys[position].Count)
            {
                path += $":LinkBaseAliases:{_aliasKeys[position][alias]}";
            }
        }

        return path;
    }

    private void ReadShards(IConfigurationSection list)
    {
        string listPath = $"{_prefix}:Shards";
        foreach ((string key, IConfigurationSection row) in Entries(list, listPath))
        {
            string path = $"{listPath}:{key}";
            if (ExpectGroupOrAbsent(row, path, allowAbsent: true) == Shape.Invalid)
            {
                continue;
            }

            var shard = new ShardOptions
            {
                Id = Scalar(row, "Id", path),
                Alias = Scalar(row, "Alias", path),
                BaseUrl = Scalar(row, "BaseUrl", path),
                Profile = Scalar(row, "Profile", path),
            };

            IConfigurationSection auth = row.GetSection("Auth");
            if (ExpectGroupOrAbsent(auth, $"{path}:Auth") == Shape.Group)
            {
                shard.Auth = new AuthOptions
                {
                    Type = Scalar(auth, "Type", $"{path}:Auth"),
                    Resource = Scalar(auth, "Resource", $"{path}:Auth"),
                    ClientId = Scalar(auth, "ClientId", $"{path}:Auth"),
                    TenantId = Scalar(auth, "TenantId", $"{path}:Auth"),
                    SecretName = Scalar(auth, "SecretName", $"{path}:Auth"),
                };
            }

            var aliasKeys = new List<string>();
            string aliasesPath = $"{path}:LinkBaseAliases";
            foreach ((string aliasKey, IConfigurationSection alias) in Entries(row.GetSection("LinkBaseAliases"), aliasesPath))
            {
                string aliasPath = $"{aliasesPath}:{aliasKey}";
                if (!TryScalar(alias, aliasPath, out string? value))
                {
                    continue;
                }

                if (value is null)
                {
                    Fail(aliasPath, "must have a value.");
                    continue;
                }

                shard.LinkBaseAliases.Add(value);
                aliasKeys.Add(aliasKey);
            }

            ShardRequestLimits? limits = ReadWireLimits(row.GetSection("WireLimits"), $"{path}:WireLimits");
            if (limits is not null && ShardId.TryParse(shard.Id, out ShardId? id))
            {
                // The first row keeps the ceiling; a repeated id is the registry's to refuse.
                _ = WireLimits.TryAdd(id, limits);
            }

            Options.Shards.Add(shard);
            _shardKeys.Add(key);
            _aliasKeys.Add(aliasKeys);
        }
    }

    private ShardRequestLimits? ReadWireLimits(IConfigurationSection section, string path)
    {
        switch (ExpectGroupOrAbsent(section, path))
        {
            case Shape.Absent:
                Fail(path, "is required.");
                return null;
            case Shape.Group:
                int? get = WireLimit(section, "MaxGetTargetBytes", path);
                int? post = WireLimit(section, "MaxPostBodyBytes", path);
                return get is { } g && post is { } p ? new ShardRequestLimits(g, p) : null;
            default:
                return null;
        }
    }

    // ASCII digits only, so no sign, white space, grouping or fallback; the parsed value must also be positive.
    private int? WireLimit(IConfigurationSection parent, string name, string parentPath)
    {
        string path = $"{parentPath}:{name}";
        if (!TryScalar(parent.GetSection(name), path, out string? text))
        {
            return null;
        }

        if (text is null)
        {
            Fail(path, "is required.");
            return null;
        }

        if (!TryIndex(text, out int value) || value <= 0)
        {
            Fail(path, "must be a positive decimal 32-bit integer.");
            return null;
        }

        return value;
    }

    private void ReadTransport(IConfigurationSection section)
    {
        IConfigurationSection timeouts = section.GetSection("Timeouts");
        string timeoutsPath = $"{_prefix}:Timeouts";
        if (ExpectGroupOrAbsent(timeouts, timeoutsPath) == Shape.Group)
        {
            if (TransportScalar(timeouts, "BackendCall", timeoutsPath) is { } backendCall)
            {
                Options.Timeouts.BackendCall = ParseTimeSpan(backendCall, $"{timeoutsPath}:BackendCall") ?? Options.Timeouts.BackendCall;
            }

            if (TransportScalar(timeouts, "Request", timeoutsPath) is { } request)
            {
                Options.Timeouts.Request = ParseTimeSpan(request, $"{timeoutsPath}:Request") ?? Options.Timeouts.Request;
            }
        }

        IConfigurationSection limits = section.GetSection("Limits");
        string limitsPath = $"{_prefix}:Limits";
        if (ExpectGroupOrAbsent(limits, limitsPath) == Shape.Group
            && TransportScalar(limits, "MaxResponseBytes", limitsPath) is { } maxResponseBytes)
        {
            if (long.TryParse(maxResponseBytes, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long bytes))
            {
                Options.Limits.MaxResponseBytes = bytes;
            }
            else
            {
                Fail($"{limitsPath}:MaxResponseBytes", "must be a decimal 64-bit integer.");
            }
        }
    }

    private string? TransportScalar(IConfigurationSection parent, string name, string parentPath)
    {
        string path = $"{parentPath}:{name}";
        if (!TryScalar(parent.GetSection(name), path, out string? value))
        {
            return null;
        }

        // The JSON provider exposes both null and omitted values as null; only a present key appears among the children.
        if (value is null && parent.GetChildren().Any(child => string.Equals(child.Key, name, StringComparison.OrdinalIgnoreCase)))
        {
            Fail(path, "must have a value when configured.");
        }

        return value;
    }

    private TimeSpan? ParseTimeSpan(string text, string path)
    {
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan value))
        {
            return value;
        }

        Fail(path, "must be a time span such as 00:00:10.");
        return null;
    }

    // Called only when a shard uses client credentials, so a managed-identity-only host never reads these keys.
    private void ReadMount(IConfigurationSection backend)
    {
        string backendPath = $"{_prefix}:Backend";
        if (ExpectGroupOrAbsent(backend, backendPath) != Shape.Group)
        {
            return;
        }

        SecretMountRoot = Scalar(backend, "SecretMountRoot", backendPath);

        string filesPath = $"{backendPath}:SecretFiles";
        var files = new List<KeyValuePair<string, string>>();
        IReadOnlyList<(string Key, IConfigurationSection Section)> rows = Entries(backend.GetSection("SecretFiles"), filesPath);
        foreach ((string key, IConfigurationSection row) in rows)
        {
            string path = $"{filesPath}:{key}";
            if (ExpectGroupOrAbsent(row, path, allowAbsent: true) == Shape.Invalid)
            {
                continue;
            }

            string? name = Scalar(row, "Name", path);
            string? fileName = Scalar(row, "FileName", path);
            if (name is null)
            {
                Fail($"{path}:Name", "is required.");
            }

            if (fileName is null)
            {
                Fail($"{path}:FileName", "is required.");
            }

            if (name is not null && fileName is not null)
            {
                files.Add(new KeyValuePair<string, string>(name, fileName));
            }
        }

        if (rows.Count > 0)
        {
            SecretFiles = files;
        }
    }

    // Ordered by numeric value; each key keeps its configured spelling (such as "01") so diagnostics name what was written.
    private IReadOnlyList<(string Key, IConfigurationSection Section)> Entries(IConfigurationSection list, string path)
    {
        if (ExpectGroupOrAbsent(list, path) != Shape.Group)
        {
            return [];
        }

        var entries = new SortedDictionary<int, IConfigurationSection>();
        bool badKey = false;
        bool repeated = false;
        foreach (IConfigurationSection child in list.GetChildren())
        {
            if (!TryIndex(child.Key, out int index))
            {
                badKey = true;
            }
            else if (!entries.TryAdd(index, child))
            {
                repeated = true;
            }
        }

        if (badKey)
        {
            Fail(path, "every entry key must be a non-negative decimal index.");
        }

        if (repeated)
        {
            Fail(path, "must not repeat an index.");
        }

        return badKey || repeated ? [] : [.. entries.Values.Select(e => (e.Key, e))];
    }

    private string? Scalar(IConfigurationSection parent, string name, string parentPath) =>
        TryScalar(parent.GetSection(name), $"{parentPath}:{name}", out string? value) ? value : null;

    // A leaf that is missing is null; a leaf that also has nested settings, or is a group, is refused.
    private bool TryScalar(IConfigurationSection section, string path, out string? value)
    {
        value = null;
        switch (Classify(section, path))
        {
            case Shape.Absent:
                return true;
            case Shape.Scalar:
                value = section.Value;
                return true;
            case Shape.Group:
                Fail(path, "must be a single value, not a group of settings.");
                return false;
            default:
                return false;
        }
    }

    // Group stays Group, an absent section stays Absent, and a scalar where a group belongs is refused as Invalid.
    private Shape ExpectGroupOrAbsent(IConfigurationSection section, string path, bool allowAbsent = false)
    {
        Shape shape = Classify(section, path);
        if (shape == Shape.Scalar)
        {
            Fail(path, "must be a group of settings, not a single value.");
            return Shape.Invalid;
        }

        return shape == Shape.Absent && allowAbsent ? Shape.Group : shape;
    }

    private Shape Classify(IConfigurationSection section, string path)
    {
        bool hasChildren = section.GetChildren().Any();
        if (section.Value is null)
        {
            return hasChildren ? Shape.Group : Shape.Absent;
        }

        if (hasChildren)
        {
            Fail(path, "must not combine a value with nested settings.");
            return Shape.Invalid;
        }

        return Shape.Scalar;
    }

    private void Fail(string path, string message) => _failures.Add($"{path}: {message}");
}

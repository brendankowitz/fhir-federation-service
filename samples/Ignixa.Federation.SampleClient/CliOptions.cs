using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.SampleClient;

internal enum ReadMode
{
    None,
    Alternate,
    Direct,
}

/// <summary>
/// Parsed command line. Credentials are never arguments: they come from the environment only, so they cannot end up
/// in shell history or process listings.
/// </summary>
internal sealed record CliOptions(
    Uri BaseUrl,
    string ResourceType,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    int MaxPages,
    ReadMode Read,
    IReadOnlyList<Uri> DirectShards,
    string? FederationToken,
    string? ShardToken)
{
    public const string BaseUrlVariable = "FEDERATION_BASE_URL";
    public const string FederationTokenVariable = "FEDERATION_ACCESS_TOKEN";
    public const string ShardTokenVariable = "SHARD_ACCESS_TOKEN";

    public const int DefaultMaxPages = 5;

    // An option whose name suggests a credential is refused outright (rather than ignored) so a secret passed by
    // habit is noticed at once; the value is never echoed.
    private static readonly string[] CredentialWords = ["token", "bearer", "authorization", "password", "secret", "credential"];

    /// <summary>Parses the arguments; on failure <paramref name="error"/> says why, without repeating any argument value.</summary>
    public static bool TryParse(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        [NotNullWhen(true)] out CliOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        string? baseText = null;
        string? type = null;
        var maxPages = DefaultMaxPages;
        var read = ReadMode.None;
        List<KeyValuePair<string, string>> parameters = [];
        List<Uri> directShards = [];

        for (var i = 0; i < args.Length; i++)
        {
            var (name, inlineValue) = Split(args[i]);
            if (!name.StartsWith("--"))
            {
                error = "unexpected argument; options start with '--'.";
                return false;
            }

            if (CredentialWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"credentials are not accepted as arguments; set {FederationTokenVariable} and/or {ShardTokenVariable} in the environment instead.";
                return false;
            }

            var value = inlineValue ?? (i + 1 < args.Length ? args[++i] : null);
            if (value is null)
            {
                error = $"{name} needs a value.";
                return false;
            }

            switch (name)
            {
                case "--base":
                    baseText = value;
                    break;
                case "--type":
                    type = value;
                    break;
                case "--param":
                    if (value.IndexOf('=') is not (> 0 and var equals))
                    {
                        error = "--param must look like name=value.";
                        return false;
                    }

                    parameters.Add(new(value[..equals], value[(equals + 1)..]));
                    break;
                case "--max-pages":
                    if (!int.TryParse(value, out maxPages) || maxPages < 1)
                    {
                        error = "--max-pages must be a whole number of at least 1.";
                        return false;
                    }

                    break;
                case "--read":
                    if (!Enum.TryParse(value, ignoreCase: true, out read) || !Enum.IsDefined(read))
                    {
                        error = "--read must be none, alternate or direct.";
                        return false;
                    }

                    break;
                case "--direct-shard":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var shard))
                    {
                        error = "--direct-shard must be an absolute https URL.";
                        return false;
                    }

                    directShards.Add(shard);
                    break;
                default:
                    error = $"unknown option {name}.";
                    return false;
            }
        }

        baseText ??= Variable(env, BaseUrlVariable);
        if (baseText is null || !Uri.TryCreate(baseText, UriKind.Absolute, out var baseUrl))
        {
            error = $"an absolute https federation URL is required: pass --base or set {BaseUrlVariable}.";
            return false;
        }

        if (type is null)
        {
            error = "--type is required, for example --type Patient.";
            return false;
        }

        error = null;
        options = new CliOptions(
            baseUrl, type, parameters, maxPages, read, directShards,
            Variable(env, FederationTokenVariable), Variable(env, ShardTokenVariable));
        return true;
    }

    private static (string Name, string? Value) Split(string arg) =>
        arg.StartsWith("--") && arg.IndexOf('=') is > 0 and var equals
            ? (arg[..equals], arg[(equals + 1)..])
            : (arg, null);

    private static string? Variable(IReadOnlyDictionary<string, string?> env, string name) =>
        env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

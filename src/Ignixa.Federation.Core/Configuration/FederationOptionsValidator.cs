using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Ignixa.Federation.Core.Shards;
using Microsoft.Extensions.Options;

namespace Ignixa.Federation.Core.Configuration;

/// <summary>
/// Validates <see cref="FederationOptions"/> at startup. Shard rules are <see cref="ShardRegistry"/>'s; this adds the
/// limit, deadline, token and chain-resolution rules. Every failure is reported at once, prefixed with its
/// configuration path (for example <c>Federation:Timeouts:BackendCall: ...</c>).
/// </summary>
public sealed partial class FederationOptionsValidator : IValidateOptions<FederationOptions>
{
    private const int SpecMaxPageSize = 100;

    private static readonly FrozenSet<string> CertifiedVersions = FrozenSet.Create(StringComparer.Ordinal, "R4");

    private readonly BackendProfile[] _profiles;

    /// <summary>Initializes a new instance of the <see cref="FederationOptionsValidator"/> class.</summary>
    /// <param name="profiles">The backend profiles a shard may name. Copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is <see langword="null"/>.</exception>
    public FederationOptionsValidator(IEnumerable<BackendProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = [.. profiles];
    }

    /// <summary>Determines whether an exact configured release has been certified for the host.</summary>
    public static bool IsCertifiedVersion(string? version) => version is not null && CertifiedVersions.Contains(version);

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, FederationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (!IsCertifiedVersion(options.Version))
        {
            failures.Add("Federation:Version: must be a certified release.");
        }

        if (!ShardRegistry.TryCreate(options.Shards, _profiles, out _, out IReadOnlyList<string> shardErrors))
        {
            failures.AddRange(shardErrors);
        }

        ValidateLimits(options.Limits, options.Shards.Count, failures);
        ValidateTimeouts(options.Timeouts, failures);
        ValidateTokens(options.Tokens, failures);
        ValidateChainResolution(options.ChainResolution, failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateLimits(LimitOptions limits, int shardCount, List<string> failures)
    {
        const string Path = "Federation:Limits";
        Positive(limits.DefaultPageSize, $"{Path}:DefaultPageSize", failures);
        Positive(limits.MaxPageSize, $"{Path}:MaxPageSize", failures);
        Positive(limits.MaxChainKeys, $"{Path}:MaxChainKeys", failures);
        Positive(limits.MaxChainSourceRows, $"{Path}:MaxChainSourceRows", failures);
        Positive(limits.MaxIncludesPerPage, $"{Path}:MaxIncludesPerPage", failures);
        Positive(limits.BackendCallsPerShard, $"{Path}:BackendCallsPerShard", failures);
        Positive(limits.MaxResponseBytes, $"{Path}:MaxResponseBytes", failures);
        Positive(limits.MaxParallelBackendCalls, $"{Path}:MaxParallelBackendCalls", failures);
        Positive(limits.ShardCountWarningThreshold, $"{Path}:ShardCountWarningThreshold", failures);

        if (limits.MaxPageSize > SpecMaxPageSize)
        {
            failures.Add($"{Path}:MaxPageSize: must be at most {SpecMaxPageSize} (design spec §7.3).");
        }

        if (limits.DefaultPageSize > limits.MaxPageSize)
        {
            failures.Add($"{Path}:DefaultPageSize: must not exceed MaxPageSize.");
        }

        // The request-wide backend-call budget is this times the shard count.
        if ((long)limits.BackendCallsPerShard * shardCount > int.MaxValue)
        {
            failures.Add($"{Path}:BackendCallsPerShard: times the shard count must fit in a 32-bit integer.");
        }
    }

    private static void ValidateTimeouts(TimeoutOptions timeouts, List<string> failures)
    {
        const string Path = "Federation:Timeouts";
        Positive(timeouts.Request, $"{Path}:Request", failures);
        Positive(timeouts.BackendCall, $"{Path}:BackendCall", failures);
        Positive(timeouts.ChainSubquery, $"{Path}:ChainSubquery", failures);

        if (timeouts.BackendCall > timeouts.Request)
        {
            failures.Add($"{Path}:BackendCall: must not exceed Request.");
        }

        if (timeouts.ChainSubquery > timeouts.Request)
        {
            failures.Add($"{Path}:ChainSubquery: must not exceed Request.");
        }
    }

    private static void ValidateTokens(TokenOptions tokens, List<string> failures)
    {
        const string Path = "Federation:Tokens";
        Positive(tokens.MaxInlineTokenBytes, $"{Path}:MaxInlineTokenBytes", failures);
        Positive(tokens.SlidingLifetime, $"{Path}:SlidingLifetime", failures);
        Positive(tokens.AbsoluteLifetime, $"{Path}:AbsoluteLifetime", failures);

        if (tokens.SlidingLifetime > tokens.AbsoluteLifetime)
        {
            failures.Add($"{Path}:SlidingLifetime: must not exceed AbsoluteLifetime.");
        }
    }

    private static void ValidateChainResolution(ChainResolutionOptions chainResolution, List<string> failures)
    {
        const string Path = "Federation:ChainResolution";
        if (chainResolution.Default != ChainResolutionMode.Distributed)
        {
            failures.Add($"{Path}:Default: must be Distributed; Passthrough is chosen per path (design spec §7.5).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < chainResolution.Passthrough.Count; i++)
        {
            string? path = chainResolution.Passthrough[i];
            if (path is null || !PassthroughPath().IsMatch(path))
            {
                failures.Add($"{Path}:Passthrough:{i}: must have the form Type.parameter:TargetType.");
            }
            else if (!seen.Add(path))
            {
                failures.Add($"{Path}:Passthrough:{i}: repeats an earlier path.");
            }
        }
    }

    private static void Positive(long value, string path, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{path}: must be positive.");
        }
    }

    private static void Positive(TimeSpan value, string path, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{path}: must be positive.");
        }
    }

    [GeneratedRegex(@"\A[A-Z][A-Za-z]{0,63}\.[A-Za-z][A-Za-z0-9_-]*:[A-Z][A-Za-z]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PassthroughPath();
}

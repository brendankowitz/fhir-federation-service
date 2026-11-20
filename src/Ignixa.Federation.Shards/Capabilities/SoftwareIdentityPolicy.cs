using System.Text.RegularExpressions;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>Matches only the software identity advertised in a shard's capability statement.</summary>
/// <remarks>Names and exact versions are compared ordinally without trimming or case folding. A match does not certify behavior.</remarks>
public sealed class SoftwareIdentityPolicy
{
    private const string Number = "(?:0|[1-9][0-9]*)";
    private const string Release = Number + "\\." + Number + "\\." + Number;
    private const string PrereleasePart = "(?:" + Number + "|[0-9]*[A-Za-z-][0-9A-Za-z-]*)";
    private const string BuildPart = "[0-9A-Za-z-]+";
    private static readonly Regex SemVer = new(
        "\\A" + Release + "(?:-" + PrereleasePart + "(?:\\." + PrereleasePart + ")*)?"
        + "(?:\\+" + BuildPart + "(?:\\." + BuildPart + ")*)?\\z",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
        Regex.InfiniteMatchTimeout);

    private readonly string _productName;
    private readonly string? _exactVersion;
    private readonly (Version MinimumInclusive, Version MaximumExclusive)? _range;

    private SoftwareIdentityPolicy(
        string productName,
        string? exactVersion,
        (Version MinimumInclusive, Version MaximumExclusive)? range)
    {
        _productName = IsProductName(productName)
            ? productName
            : throw new ArgumentException("Supply an unpadded software name without control characters.", nameof(productName));
        _exactVersion = exactVersion;
        _range = range;
    }

    /// <summary>Creates a policy for one product name and one exact SemVer 2.0.0 identity, including prerelease and build text.</summary>
    /// <exception cref="ArgumentException">The name or version is invalid.</exception>
    public static SoftwareIdentityPolicy Exact(string productName, string version)
    {
        if (version is null || !SemVer.IsMatch(version))
        {
            throw new ArgumentException("Supply a SemVer 2.0.0 identity.", nameof(version));
        }

        return new SoftwareIdentityPolicy(productName, version, range: null);
    }

    /// <summary>Creates a policy for one product name and a half-open range of plain releases.</summary>
    /// <remarks>
    /// Bounds and eligible versions are SemVer major.minor.patch with each component fitting in <see cref="int"/>.
    /// The lower bound is inclusive and the upper bound exclusive. Prerelease and build text require an exact policy.
    /// </remarks>
    /// <exception cref="ArgumentException">The name or either bound is invalid, or the bounds are not ascending.</exception>
    public static SoftwareIdentityPolicy ReleaseRange(string productName, string minimumInclusive, string maximumExclusive)
    {
        Version minimum = ParseBound(minimumInclusive, nameof(minimumInclusive));
        Version maximum = ParseBound(maximumExclusive, nameof(maximumExclusive));
        if (minimum >= maximum)
        {
            throw new ArgumentException("The upper bound must be greater than the lower bound.", nameof(maximumExclusive));
        }

        return new SoftwareIdentityPolicy(productName, exactVersion: null, (minimum, maximum));
    }

    /// <summary>Compares only the fields reported in <see cref="ShardCapabilityEvidence.Software"/>.</summary>
    public SoftwareIdentityMatch Match(ShardCapabilityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (evidence.Software is not { Name: { } name, Version: { } version })
        {
            return SoftwareIdentityMatch.MissingIdentity;
        }

        if (!IsProductName(name) || !SemVer.IsMatch(version))
        {
            return SoftwareIdentityMatch.MalformedIdentity;
        }

        if (!string.Equals(name, _productName, StringComparison.Ordinal))
        {
            return SoftwareIdentityMatch.ProductMismatch;
        }

        bool matches = _range is { } range
            ? Version.TryParse(version, out Version? release) && release >= range.MinimumInclusive && release < range.MaximumExclusive
            : string.Equals(version, _exactVersion, StringComparison.Ordinal);
        return matches ? SoftwareIdentityMatch.Matched : SoftwareIdentityMatch.VersionMismatch;
    }

    private static bool IsProductName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.AsSpan().Trim().Length == name.Length && !name.Any(char.IsControl);

    private static Version ParseBound(string value, string parameterName)
    {
        if (value is not null && SemVer.IsMatch(value) && Version.TryParse(value, out Version? bound))
        {
            return bound;
        }

        throw new ArgumentException("Supply a plain SemVer release with Int32 components.", parameterName);
    }
}

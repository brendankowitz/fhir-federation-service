using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Ignixa.Federation.Core.Identity;

/// <summary>
/// A FHIR R4 logical id (design spec §4.1, §7.7): 1–64 ASCII letters, digits, <c>-</c> or <c>.</c>.
/// Compared ordinally and case-sensitively; never case-folded.
/// </summary>
/// <remarks>
/// <c>.</c> and <c>..</c> are valid ids; build URLs from them by concatenation, not relative URI resolution. A sealed
/// record with a private constructor, so <c>default</c> is <see langword="null"/> rather than an instance with a
/// null value.
/// </remarks>
public sealed partial record ResourceId
{
    private ResourceId(string value) => Value = value;

    /// <summary>Gets the id text.</summary>
    public string Value { get; }

    /// <summary>Parses a logical id.</summary>
    /// <param name="value">The id text.</param>
    /// <returns>The logical id.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a FHIR R4 id.</exception>
    public static ResourceId Parse(string value)
    {
        string? error = Validate(value);
        return error is null ? new ResourceId(value) : throw new ArgumentException($"Invalid resource id: {error}", nameof(value));
    }

    /// <summary>Tries to parse a logical id.</summary>
    /// <param name="value">The id text.</param>
    /// <param name="result">The logical id, when <paramref name="value"/> is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ResourceId? result)
    {
        result = Validate(value) is null ? new ResourceId(value!) : null;
        return result is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static string? Validate(string? value)
    {
        return value is not null && Pattern().IsMatch(value)
            ? null
            : "must be 1-64 ASCII letters, digits, '-' or '.'.";
    }

    [GeneratedRegex(@"\A[A-Za-z0-9\-.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

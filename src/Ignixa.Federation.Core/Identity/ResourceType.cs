using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Ignixa.Federation.Core.Identity;

/// <summary>
/// A FHIR resource type name segment, such as <c>Patient</c> (design spec §4.1). Compared ordinally and
/// case-sensitively.
/// </summary>
/// <remarks>
/// The check is lexical (an uppercase ASCII letter, then up to 63 ASCII letters). A parsed type is not a claim that
/// the federation supports searching it. A sealed record with a private constructor, so <c>default</c> is
/// <see langword="null"/> rather than an instance with a null value.
/// </remarks>
public sealed partial record ResourceType
{
    private ResourceType(string value) => Value = value;

    /// <summary>Gets the type name.</summary>
    public string Value { get; }

    /// <summary>Parses a resource type name.</summary>
    /// <param name="value">The type name.</param>
    /// <returns>The resource type.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a type name segment.</exception>
    public static ResourceType Parse(string value)
    {
        string? error = Validate(value);
        return error is null ? new ResourceType(value) : throw new ArgumentException($"Invalid resource type: {error}", nameof(value));
    }

    /// <summary>Tries to parse a resource type name.</summary>
    /// <param name="value">The type name.</param>
    /// <param name="result">The resource type, when <paramref name="value"/> is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ResourceType? result)
    {
        result = Validate(value) is null ? new ResourceType(value!) : null;
        return result is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    private static string? Validate(string? value)
    {
        return value is not null && Pattern().IsMatch(value)
            ? null
            : "must be an uppercase ASCII letter followed by up to 63 ASCII letters.";
    }

    [GeneratedRegex(@"\A[A-Z][A-Za-z]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

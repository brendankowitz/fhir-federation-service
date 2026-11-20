using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The short, URL-safe name of a shard (design spec §4.2): the proxied-read route segment, the
/// <c>source-shard</c> extension value and the label in logs and telemetry.
/// </summary>
/// <remarks>
/// One spelling only: 1–32 characters of lowercase ASCII letters, digits, <c>_</c> or <c>-</c>, starting with a
/// letter or digit. Other spellings are rejected, never lowercased. A sealed record with a private constructor, so
/// <c>default</c> is <see langword="null"/> rather than an instance with a null value.
/// </remarks>
public sealed partial record ShardAlias
{
    private ShardAlias(string value) => Value = value;

    /// <summary>Gets the alias text.</summary>
    public string Value { get; }

    /// <summary>Parses a shard alias.</summary>
    /// <param name="value">The alias text.</param>
    /// <returns>The shard alias.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid shard alias.</exception>
    public static ShardAlias Parse(string value)
    {
        string? error = Validate(value);
        return error is null ? new ShardAlias(value) : throw new ArgumentException($"Invalid shard alias: {error}", nameof(value));
    }

    /// <summary>Tries to parse a shard alias.</summary>
    /// <param name="value">The alias text.</param>
    /// <param name="result">The shard alias, when <paramref name="value"/> is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ShardAlias? result)
    {
        result = Validate(value) is null ? new ShardAlias(value!) : null;
        return result is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Returns why <paramref name="value"/> is not a valid shard alias, or <see langword="null"/> when it is.</summary>
    internal static string? Validate(string? value)
    {
        return value is not null && Pattern().IsMatch(value)
            ? null
            : "must be 1-32 characters of lowercase ASCII letters, digits, '_' or '-', starting with a letter or digit.";
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

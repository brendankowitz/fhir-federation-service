using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The stable identity of a shard in the registry (design spec §4.2). Compared ordinally and case-sensitively.
/// Not client-visible: routes, extensions and logs use <see cref="ShardAlias"/>.
/// </summary>
/// <remarks>
/// A sealed record with a private constructor: equality is generated and ordinal, and <c>default</c> is
/// <see langword="null"/>, so no instance can hold an unvalidated value.
/// </remarks>
public sealed record ShardId
{
    private const int MaxLength = 64;

    private ShardId(string value) => Value = value;

    /// <summary>Gets the identifier text: not blank, no leading or trailing whitespace, at most 64 characters.</summary>
    public string Value { get; }

    /// <summary>Parses a shard identifier.</summary>
    /// <param name="value">The identifier text.</param>
    /// <returns>The shard identifier.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid shard identifier.</exception>
    public static ShardId Parse(string value)
    {
        string? error = Validate(value);
        return error is null ? new ShardId(value) : throw new ArgumentException($"Invalid shard id: {error}", nameof(value));
    }

    /// <summary>Tries to parse a shard identifier.</summary>
    /// <param name="value">The identifier text.</param>
    /// <param name="result">The shard identifier, when <paramref name="value"/> is valid.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out ShardId? result)
    {
        result = Validate(value) is null ? new ShardId(value!) : null;
        return result is not null;
    }

    /// <inheritdoc/>
    public override string ToString() => Value;

    /// <summary>Returns why <paramref name="value"/> is not a valid shard identifier, or <see langword="null"/> when it is.</summary>
    internal static string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "must not be blank.";
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return "must not have leading or trailing whitespace.";
        }

        return value.Length <= MaxLength ? null : $"must be at most {MaxLength} characters.";
    }
}

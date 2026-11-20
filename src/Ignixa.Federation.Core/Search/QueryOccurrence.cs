namespace Ignixa.Federation.Core.Search;

/// <summary>
/// One <c>key=value</c> pair of a <see cref="SearchQuery"/>: the exact wire text, its position, and the text decoded once
/// (design spec §6.1).
/// </summary>
/// <remarks>Created only by <see cref="SearchQuery.Parse"/>, so <see cref="Key"/> and <see cref="Value"/> always decode <see cref="Raw"/>.</remarks>
public sealed record QueryOccurrence
{
    internal QueryOccurrence(int ordinal, ParameterOccurrence raw, string key, string value)
    {
        Ordinal = ordinal;
        Raw = raw;
        Key = key;
        Value = value;
    }

    /// <summary>Gets the zero-based position among the query's occurrences, the identity a rewrite edits by.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the pair exactly as it arrived: key and value still encoded, percent casing and order kept.</summary>
    public ParameterOccurrence Raw { get; }

    /// <summary>Gets the key after one percent-decoding, <c>+</c> read as a space; case and modifiers kept, such as <c>Name:exact</c>.</summary>
    public string Key { get; }

    /// <summary>
    /// Gets the value after one percent-decoding, <c>+</c> read as a space and <c>%2B</c> as a plus. A backslash escape is
    /// kept as written (<c>a\,b</c>); splitting on commas is the planner's job. May be empty.
    /// </summary>
    public string Value { get; }

    /// <summary>Formats the occurrence's position without any query text.</summary>
    /// <returns>The occurrence ordinal.</returns>
    public override string ToString() => FormattableString.Invariant($"Query occurrence {Ordinal}");
}

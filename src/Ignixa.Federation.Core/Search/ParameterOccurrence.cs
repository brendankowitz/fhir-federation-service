namespace Ignixa.Federation.Core.Search;

/// <summary>
/// One <c>key=value</c> occurrence from a FHIR search query, exactly as written: the original key text (modifiers included)
/// and the still-encoded value text (design spec §6.2, §7). Nothing decodes, trims or splits it, so a backend receives
/// the caller's text unchanged. Order and duplicates belong to the list that holds the occurrences.
/// </summary>
/// <remarks>
/// An occurrence is a single pair already split from a query: a raw <c>&amp;</c> or <c>#</c> in either part, or a raw
/// <c>=</c> in the key, could not survive that split, and writing it to the wire as given would add parameters (a key
/// <c>x&amp;_count</c> would send a second <c>_count</c>). Such text is rejected at construction, the only way to create or
/// change an occurrence. A raw <c>=</c> in the value is legal, because a pair splits on its first <c>=</c>; encoded
/// delimiters such as <c>%26</c>, <c>%3D</c> and <c>%23</c> are ordinary text and stay unchanged.
/// </remarks>
/// <param name="Key">The key text, such as <c>name:exact</c>. Not empty; no raw <c>&amp;</c>, <c>=</c> or <c>#</c>.</param>
/// <param name="Value">The encoded value text, such as <c>a%2Cb</c>. May be empty; no raw <c>&amp;</c> or <c>#</c>.</param>
public sealed record ParameterOccurrence(string Key, string Value)
{
    /// <summary>Gets the key text, such as <c>name:exact</c>. Never empty; never contains a raw <c>&amp;</c>, <c>=</c> or <c>#</c>.</summary>
    public string Key { get; } =
        string.IsNullOrEmpty(Key) ? throw new ArgumentException("Parameter key must not be empty.", nameof(Key))
        : Key.AsSpan().IndexOfAny("&=#") >= 0
            ? throw new ArgumentException("Parameter key must be one split key, without a raw '&', '=' or '#'.", nameof(Key))
        : Key;

    /// <summary>
    /// Gets the encoded value text, such as <c>a%2Cb</c>. May be empty, never <see langword="null"/>; never contains a raw
    /// <c>&amp;</c> or <c>#</c>.
    /// </summary>
    public string Value { get; } =
        Value is null ? throw new ArgumentNullException(nameof(Value))
        : Value.AsSpan().IndexOfAny("&#") >= 0
            ? throw new ArgumentException("Parameter value must be one split value, without a raw '&' or '#'.", nameof(Value))
        : Value;
}
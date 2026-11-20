namespace Ignixa.Federation.Core.Search;

/// <summary>
/// One <c>key=value</c> occurrence from a FHIR search query, exactly as written: the original key text (modifiers included)
/// and the still-encoded value text (design spec §6.2, §7). Nothing decodes, trims or splits it, so a backend receives
/// the caller's text unchanged. Order and duplicates belong to the list that holds the occurrences.
/// </summary>
/// <param name="Key">The key text, such as <c>name:exact</c>. Not empty.</param>
/// <param name="Value">The encoded value text, such as <c>a%2Cb</c>. May be empty.</param>
public sealed record ParameterOccurrence(string Key, string Value)
{
    /// <summary>Gets the key text, such as <c>name:exact</c>. Never empty.</summary>
    public string Key { get; } = string.IsNullOrEmpty(Key) ? throw new ArgumentException("Parameter key must not be empty.", nameof(Key)) : Key;

    /// <summary>Gets the encoded value text, such as <c>a%2Cb</c>. May be empty, never <see langword="null"/>.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));
}

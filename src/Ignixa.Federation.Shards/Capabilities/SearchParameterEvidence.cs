namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// One search parameter a shard's CapabilityStatement lists: the R4 wire fields <c>name</c>, <c>type</c> and the optional
/// <c>definition</c>, exactly as reported.
/// </summary>
/// <remarks>
/// Only the reader creates instances. The values are lexical evidence: nothing is case-folded, trimmed, dereferenced or
/// matched against a canonical, and listing a parameter is not a certification that it behaves as the spec says.
/// </remarks>
public sealed record SearchParameterEvidence
{
    internal SearchParameterEvidence(string code, SearchParameterType type, string? definition)
    {
        Code = code;
        Type = type;
        Definition = definition;
    }

    /// <summary>Gets the parameter code (<c>name</c> on the wire), with its exact spelling and case.</summary>
    public string Code { get; }

    /// <summary>Gets the reported parameter type.</summary>
    public SearchParameterType Type { get; }

    /// <summary>
    /// Gets the reported absolute <c>definition</c> canonical, with any version or fragment, exactly as sent; or
    /// <see langword="null"/> when the shard reported none. A malformed definition is refused by the reader, never
    /// reported as <see langword="null"/>.
    /// </summary>
    public string? Definition { get; }
}

namespace Ignixa.Federation.Core.Search;

/// <summary>A search request the federation refuses, ready to become a 400 OperationOutcome.</summary>
/// <remarks>
/// Built only from a <see cref="SearchFailureCode"/> and a pair position, never from the caller's text, so
/// <see cref="Message"/> is safe to return or log: query values can be PHI.
/// </remarks>
public sealed record SearchFailure
{
    internal SearchFailure(SearchFailureCode code, int ordinal)
    {
        Code = code;
        Ordinal = ordinal;
        Message = $"Search parameter {ordinal} is not valid: {Describe(code)}";
    }

    /// <summary>Gets the reason.</summary>
    public SearchFailureCode Code { get; }

    /// <summary>Gets the zero-based position of the offending <c>key=value</c> pair in the query, counting every <c>&amp;</c>-separated segment.</summary>
    public int Ordinal { get; }

    /// <summary>Gets a fixed, PHI-free description for the OperationOutcome diagnostics.</summary>
    public string Message { get; }

    private static string Describe(SearchFailureCode code) => code switch
    {
        SearchFailureCode.MalformedPercentEncoding => "a '%' is not followed by two hexadecimal digits.",
        SearchFailureCode.InvalidUtf8 => "the percent-encoded bytes are not valid UTF-8.",
        SearchFailureCode.InvalidCharacter => "it holds a raw character that must be percent-encoded.",
        SearchFailureCode.EmptyPair => "it is empty.",
        SearchFailureCode.MissingEquals => "it has no '='.",
        SearchFailureCode.EmptyKey => "its name is empty.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Undefined failure code."),
    };
}

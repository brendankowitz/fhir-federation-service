namespace Ignixa.Federation.Core.Search;

/// <summary>A search request the federation refuses, ready to become an OperationOutcome.</summary>
/// <remarks>
/// Built only from a <see cref="SearchFailureCode"/> and a pair position, never from the caller's text, so
/// <see cref="Message"/> is safe to return or log: query values can be PHI.
/// </remarks>
public sealed record SearchFailure
{
    /// <summary>The <see cref="Ordinal"/> of a failure that concerns the whole request rather than one pair.</summary>
    public const int NoOrdinal = -1;

    internal SearchFailure(SearchFailureCode code, int ordinal)
    {
        Code = code;
        Ordinal = ordinal;
        IsNotSupported = code is SearchFailureCode.UnsupportedResourceType or SearchFailureCode.UnsupportedParameter
            or SearchFailureCode.UnsupportedModifier or SearchFailureCode.UnsupportedRepetition or SearchFailureCode.UnsupportedAlternatives
            or SearchFailureCode.UnsupportedChain or SearchFailureCode.UnsupportedInclude or SearchFailureCode.UnsupportedSort
            or SearchFailureCode.UnsupportedFormat or SearchFailureCode.ExactCountNotCertified;
        string description = Describe(code);
        Message = ordinal == NoOrdinal
            ? $"The search is not supported: {description}"
            : $"Search parameter {ordinal} is {(IsNotSupported ? "not supported" : "not valid")}: {description}";
    }

    /// <summary>Gets the reason.</summary>
    public SearchFailureCode Code { get; }

    /// <summary>
    /// Gets the zero-based position of the offending <c>key=value</c> pair in the query, counting every <c>&amp;</c>-separated
    /// segment, or <see cref="NoOrdinal"/> when the failure concerns the whole request.
    /// </summary>
    public int Ordinal { get; }

    /// <summary>
    /// Gets a value indicating whether the request is well formed but beyond what every shard is proven to answer exactly,
    /// as opposed to invalid.
    /// </summary>
    public bool IsNotSupported { get; }

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
        SearchFailureCode.InvalidValue => "its value is not valid for the parameter.",
        SearchFailureCode.DuplicateControl => "the control may appear only once.",
        SearchFailureCode.ContinuationNotAlone => "a continuation token must be the only parameter.",
        SearchFailureCode.ChainTargetAmbiguous => "the chained reference may point at more than one resource type; name the type.",
        SearchFailureCode.UnsupportedResourceType => "the resource type is not searchable on every shard.",
        SearchFailureCode.UnsupportedParameter => "the parameter is not supported on every shard.",
        SearchFailureCode.UnsupportedModifier => "the modifier is not supported on every shard.",
        SearchFailureCode.UnsupportedRepetition => "repeating the parameter is not supported on every shard.",
        SearchFailureCode.UnsupportedAlternatives => "alternative values are not supported on every shard.",
        SearchFailureCode.UnsupportedChain => "the chain is not supported; one certified reference hop is the limit.",
        SearchFailureCode.UnsupportedInclude => "the include is not supported on every shard.",
        SearchFailureCode.UnsupportedSort => "only _lastUpdated ascending or descending is supported.",
        SearchFailureCode.UnsupportedFormat => "only JSON is supported.",
        SearchFailureCode.ExactCountNotCertified => "an exact count is not certified for this query on every shard.",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Undefined failure code."),
    };
}

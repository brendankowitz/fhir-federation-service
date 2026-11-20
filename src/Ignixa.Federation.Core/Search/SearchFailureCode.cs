namespace Ignixa.Federation.Core.Search;

/// <summary>Why a search request was refused, for the 400 response. The reasons are stable; the host maps each to an OperationOutcome.</summary>
public enum SearchFailureCode
{
    /// <summary>A <c>%</c> is not followed by two hexadecimal digits.</summary>
    MalformedPercentEncoding,

    /// <summary>Percent-decoded bytes are not valid UTF-8.</summary>
    InvalidUtf8,

    /// <summary>The wire text holds a raw character a query string cannot carry unambiguously: a space, control, non-ASCII or <c>#</c>.</summary>
    InvalidCharacter,

    /// <summary>Two <c>&amp;</c> delimiters have nothing between them, or the text starts or ends with one.</summary>
    EmptyPair,

    /// <summary>A pair has no <c>=</c>, so it is neither a key with a value nor a key with an empty value.</summary>
    MissingEquals,

    /// <summary>A pair has nothing before its first <c>=</c>.</summary>
    EmptyKey,
}

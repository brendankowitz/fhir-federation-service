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

    /// <summary>
    /// A value is empty or malformed for its parameter, or names something R4 does not define for it. A reference value
    /// is malformed unless it is an id, <c>Type/id</c> (optionally <c>/_history/version</c>) for an R4 type, or an
    /// absolute URI (optionally <c>|version</c>), even where the compiler accepts it; contrast
    /// <see cref="UnsupportedReferenceFilter"/>.
    /// </summary>
    InvalidValue,

    /// <summary>A control that may appear once, such as <c>_count</c> or <c>_sort</c>, is repeated.</summary>
    DuplicateControl,

    /// <summary>A <c>ct</c> continuation token appears with other parameters.</summary>
    ContinuationNotAlone,

    /// <summary>An untyped chain hop crosses a reference R4 allows to point at more than one resource type.</summary>
    ChainTargetAmbiguous,

    /// <summary>Not every shard searches the resource type.</summary>
    UnsupportedResourceType,

    /// <summary>The parameter is unknown to R4, not understood, or not advertised by every shard.</summary>
    UnsupportedParameter,

    /// <summary>Not every shard certifies the modifier for the parameter's type.</summary>
    UnsupportedModifier,

    /// <summary>Not every shard certifies AND repetition for the parameter's type.</summary>
    UnsupportedRepetition,

    /// <summary>Not every shard certifies comma-separated OR alternatives for the parameter's type.</summary>
    UnsupportedAlternatives,

    /// <summary>The chain is deeper than one hop, has an unusable terminal, or its path is not certified on every shard.</summary>
    UnsupportedChain,

    /// <summary>The include or revinclude is not advertised, not certified, or iterates.</summary>
    UnsupportedInclude,

    /// <summary>The sort is not <c>_lastUpdated</c> ascending or descending with stable ties on every shard.</summary>
    UnsupportedSort,

    /// <summary>The requested format is not JSON.</summary>
    UnsupportedFormat,

    /// <summary>A count was requested but not every shard certifies an exact count for the query's shape.</summary>
    ExactCountNotCertified,

    /// <summary>
    /// A well-formed reference value, as a parameter or a chain terminal, such as <c>subject=Patient/1</c> or
    /// <c>subject:Patient.organization=Organization/1</c>. Relative, typed id, absolute and canonical values are all
    /// refused: which resource a stored reference names depends on the shard holding it, so no shard can match one exactly
    /// for the federation. A malformed reference value is <see cref="InvalidValue"/> instead. <c>:missing</c> on a reference
    /// parameter is not a reference value.
    /// </summary>
    UnsupportedReferenceFilter,
}

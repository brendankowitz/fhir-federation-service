namespace Ignixa.Federation.Core.Search;

/// <summary>What a backend request does with a query occurrence, addressed by its original ordinal.</summary>
public enum OccurrenceAction
{
    /// <summary>Forward the occurrence's original wire text unchanged.</summary>
    Keep,

    /// <summary>Leave the occurrence out; the federation applies it itself.</summary>
    Remove,

    /// <summary>
    /// Replace the occurrence with what the federation resolves, such as the matching references for a Distributed chain.
    /// The occurrence must never be forwarded or silently dropped.
    /// </summary>
    Replace,
}

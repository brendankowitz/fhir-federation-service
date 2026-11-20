namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The outcome of issuing a continuation (design spec §7.4). Transport-independent; the HTTP status each failure maps
/// to is noted on the member.
/// </summary>
internal enum ContinuationIssuanceOutcome
{
    /// <summary>A token was issued, inline or as a cached reference.</summary>
    Issued,

    /// <summary>The snapshot is over the cached plaintext limit; no token can carry it (500, a federation defect).</summary>
    TooLarge,

    /// <summary>
    /// No successor can be returned (410): a validity window has closed, or the predecessor's cached entry no longer exists.
    /// </summary>
    Gone,

    /// <summary>
    /// The continuation store is unavailable or an operation's outcome is unknown (503). Nothing was published; any
    /// entry already written expires with its own deadline.
    /// </summary>
    StoreUnavailable,
}

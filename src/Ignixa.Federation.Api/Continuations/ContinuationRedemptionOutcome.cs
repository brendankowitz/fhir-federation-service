namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The outcome of redeeming a presented continuation (design spec §7.4). Transport-independent; the HTTP status each
/// failure maps to is noted on the member.
/// </summary>
internal enum ContinuationRedemptionOutcome
{
    /// <summary>The token is authentic, bound to this caller, live, and none of its dependencies changed.</summary>
    Redeemed,

    /// <summary>
    /// Unprotect failure (400): blank, oversize, not canonical Base64Url, not authentic under this key ring and
    /// purpose, or not a well-formed format v1 envelope.
    /// </summary>
    Invalid,

    /// <summary>The token is authentic but was issued to a different caller binding (403).</summary>
    CallerMismatch,

    /// <summary>
    /// The search must be restarted (410): no token was presented, the token is outside its validity window, or a
    /// shard or parameter definition it depends on changed or disappeared.
    /// </summary>
    Gone,

    /// <summary>
    /// A cached reference is authentic, bound to this caller, and inside its window, but the continuation store is
    /// unavailable or did not answer in time (503); the same token may succeed later.
    /// </summary>
    StoreUnavailable,
}

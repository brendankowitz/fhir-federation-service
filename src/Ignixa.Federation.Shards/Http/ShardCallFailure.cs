namespace Ignixa.Federation.Shards.Http;

/// <summary>Why a shard call failed (design spec §8). Carried by <see cref="ShardCallException"/>.</summary>
public enum ShardCallFailure
{
    /// <summary>Not classified: set only by the standard exception constructors. <see cref="HttpShardClient"/> never throws it.</summary>
    Unclassified,

    /// <summary>The backend refused the credential: HTTP 401 or 403.</summary>
    Authentication,

    /// <summary>The backend answered with an HTTP status that has no specific meaning for this call.</summary>
    UnexpectedStatus,

    /// <summary>The backend answered with a 3xx redirect. Redirects are never followed, so a credential cannot leave the shard.</summary>
    Redirect,

    /// <summary>The whole call, from credential acquisition to the last body byte, outlasted the per-call deadline.</summary>
    Timeout,

    /// <summary>The connection, TLS handshake or body stream failed.</summary>
    Transport,

    /// <summary>The response body was larger than the per-call byte limit, whether or not it declared a length.</summary>
    ResponseTooLarge,

    /// <summary>
    /// The body is not a well-formed searchset of the requested type or a JSON resource with the requested read identity.
    /// </summary>
    MalformedResponse,

    /// <summary>
    /// The first page's <c>self</c> link does not report exactly the sent parameters, so the backend may have dropped or
    /// added a predicate (design spec §6.2). The results cannot be trusted as the answer to the search sent.
    /// </summary>
    SelfLinkMismatch,

    /// <summary>
    /// The <c>next</c> link is not a continuation of this shard's typed search (another origin, path or scheme, an
    /// ambiguous form) or repeats the query just sent. It is never followed. A valid link whose redemption would exceed the
    /// shard's <see cref="ShardRequestLimits"/> is also this failure, raised when the cursor is redeemed (not when the link
    /// arrives), before anything is sent; the link cannot be used on this shard.
    /// </summary>
    InvalidNextLink,

    /// <summary>A qualified current-resource read got HTTP 404: the shard has no such resource. Never set for a search.</summary>
    ResourceNotFound,

    /// <summary>A qualified current-resource read got HTTP 410: the shard says the resource was deleted. Never set for a search.</summary>
    ResourceGone,

    /// <summary>
    /// The shard's circuit breaker is open after repeated transport faults, timeouts or transient statuses, so nothing was
    /// sent to the backend and nothing was metered. The call may succeed once the break period has passed. Never set for
    /// <see cref="HttpShardClient.GetCapabilitiesAsync"/>, which bypasses the breaker (design spec §8).
    /// </summary>
    CircuitOpen,

    /// <summary>
    /// A generated search exceeds the <see cref="ShardRequestLimits"/> of its method. No credential was acquired, nothing
    /// was sent, and retrying it unchanged cannot help. Oversized backend continuations are <see cref="InvalidNextLink"/>.
    /// </summary>
    RequestTooLarge,

    /// <summary>
    /// The shard's credential could not be acquired, so the request was never sent. Classified by metadata fetches and
    /// startup probes so an operational outage stays local to the refresh or probe step. It says nothing about the backend
    /// or why the credential was unavailable.
    /// </summary>
    CredentialUnavailable,
}

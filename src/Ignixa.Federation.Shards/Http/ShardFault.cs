namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// What a failed exchange says about the backend's health, assigned only where <see cref="HttpShardClient"/> observes the
/// wire or the status. Retry and the circuit breaker read this tag and never infer it from an exception's type, so an
/// exception that merely looks like a wire fault (a meter's or credential provider's own) is never counted or retried.
/// </summary>
internal enum ShardFault
{
    /// <summary>
    /// Nothing is known about the backend: the exception was not made by <see cref="HttpShardClient"/> (a meter's or
    /// credential provider's own <see cref="ShardCallException"/>, say). Never retried and never counted against the
    /// backend, but it is not proof of life either, so it cannot close the circuit from half open.
    /// </summary>
    None,

    /// <summary>The backend answered, though not with what was asked for: not retried, and the circuit breaker sees a live backend.</summary>
    Answered,

    /// <summary>A transient status or wire fault that a repeated GET may not meet again: retried once, and counted by the breaker.</summary>
    Transient,

    /// <summary>The call outlasted its deadline: never retried, because no time is left, but counted by the breaker.</summary>
    TimedOut,
}

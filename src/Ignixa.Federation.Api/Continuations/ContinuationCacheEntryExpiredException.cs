using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The expected refusal of an <see cref="IContinuationCache"/> write whose absolute expiration is at or before the
/// backend's clock when the write linearizes: nothing was stored, so the entry can never be read. Distinct from
/// <see cref="ContinuationCacheUnavailableException"/>, whose outcome is unknown. Deliberately carries no message detail,
/// inner exception, key, or value.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Only the payload-free form may exist; a message or inner exception would carry backend or token detail.")]
internal sealed class ContinuationCacheEntryExpiredException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ContinuationCacheEntryExpiredException"/> class.</summary>
    public ContinuationCacheEntryExpiredException()
        : base("The continuation store refused a write whose expiration had already passed.")
    {
    }
}

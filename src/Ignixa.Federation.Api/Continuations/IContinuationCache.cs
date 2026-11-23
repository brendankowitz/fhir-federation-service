using Microsoft.Extensions.Caching.Distributed;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The distributed store for cached continuation values (design spec §7.4): an <see cref="IDistributedCache"/> plus one
/// conditional expiry operation on the same store and key space. This is a provider contract, not a convenience
/// wrapper; an implementation must provide every guarantee below natively and atomically. It must not be emulated
/// over a generic <see cref="IDistributedCache"/> by reading and re-writing an entry, which can resurrect an evicted
/// key, lengthen a lifetime, or lose a concurrent shortening.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consistency.</b> Every member addresses the same store and key space and is linearizable per key: once an
/// operation has returned, every later operation on that key, from any instance, observes it.
/// </para>
/// <para>
/// <b>Expiry.</b> Only <see cref="DistributedCacheEntryOptions.AbsoluteExpiration"/> is ever written; an entry is never
/// readable at or after its expiry by the backend's clock. Reads never extend or refresh an expiry.
/// </para>
/// <para>
/// <b>Reads.</b> <see cref="IDistributedCache.GetAsync"/> returns <see langword="null"/> only for a confirmed miss or
/// expired entry. It never materializes more than <see cref="MaxValueBytes"/> bytes; a larger stored value is
/// reported as unavailable.
/// </para>
/// <para>
/// <b>Writes.</b> <see cref="IDistributedCache.SetAsync"/> decides expiry at the write's linearization point, by the
/// backend's clock: when the absolute expiration is at or before that instant, it stores nothing and throws
/// <see cref="ContinuationCacheEntryExpiredException"/>. It never reports such a write as a success, which would publish
/// a token known to be dead.
/// </para>
/// <para>
/// <b>Failures.</b> When the outcome of an operation is unknown or the backend cannot be reached — a connection,
/// timeout, protocol, or server error, or a response that was lost after the operation may have been applied — the
/// provider translates it into <see cref="ContinuationCacheUnavailableException"/>, which carries no backend detail.
/// Callers treat any other exception as a provider defect and let it propagate.
/// </para>
/// <para>
/// <b>Cancellation.</b> Required: once the supplied token is canceled, the operation completes or throws
/// <see cref="OperationCanceledException"/> promptly, within a bounded latency, and any callback the provider registers
/// on the token never throws. Callers rely on this to bound a store call; they never assume a canceled operation was
/// abandoned. An operation may still take effect after cancellation, and callers discard a result that completes after
/// they have canceled or timed out; every guarantee above holds for such an operation.
/// </para>
/// </remarks>
internal interface IContinuationCache : IDistributedCache
{
    /// <summary>Gets the largest value, in bytes, the provider stores and returns.</summary>
    int MaxValueBytes { get; }

    /// <summary>
    /// Atomically shortens a live entry's expiry to at most <paramref name="maximumRemainingLifetime"/> from the
    /// backend's current time: the new expiry is <c>min(existing expiry, backend now + maximumRemainingLifetime)</c>.
    /// Never creates, rewrites, or lengthens an entry, so concurrent calls from instances with different clocks keep
    /// the earliest expiry, and a retry after an uncertain outcome is safe.
    /// </summary>
    /// <param name="key">The entry key.</param>
    /// <param name="maximumRemainingLifetime">The longest remaining lifetime the entry may keep; positive.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the entry was live and now expires no later than the limit;
    /// <see langword="false"/> when no live entry exists (never written, evicted, or expired).
    /// </returns>
    /// <exception cref="ContinuationCacheUnavailableException">The backend is unavailable or the outcome is unknown.</exception>
    Task<bool> TryShortenExpiryAsync(string key, TimeSpan maximumRemainingLifetime, CancellationToken cancellationToken);
}

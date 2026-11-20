using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using Ignixa.Federation.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// Issues and redeems continuation tokens (design spec §7.4): inline tokens through <see cref="ContinuationProtector"/>
/// while the snapshot fits, and otherwise an opaque reference to a protected value held in an
/// <see cref="IContinuationCache"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference format v1.</b> <c>r1.</c> followed by unpadded Base64Url of a Data Protection payload under
/// <see cref="ReferencePurpose"/>, which is authenticated encryption: the reference is confidential and tamper-evident,
/// not merely signed. Its plaintext is a <see cref="ContinuationEnvelope"/> with no dependencies whose snapshot is a
/// 32-byte random handle id; it carries the chain timing and caller binding, so caller, authenticity, and window are
/// decided without the store. Inline tokens are pure Base64Url and never contain <c>.</c>, so the prefix is
/// unambiguous.
/// </para>
/// <para>
/// <b>Cached value.</b> The full envelope (dependencies and snapshot), protected under <see cref="ValuePurpose"/> with
/// the handle id as a subpurpose, so a value only authenticates for its own reference. It is stored under
/// <see cref="CacheKeyPrefix"/> plus the SHA-256 of the handle id with an absolute expiry at the token's deadline, and
/// is accepted only when its timing and binding equal the reference's.
/// </para>
/// <para>
/// <b>Consistency.</b> A redemption reads the store once; an accepted value linearizes at that successful read, after
/// which the window is re-checked against the clock. A successor is written and must be durable before its cached
/// predecessor's entry is shortened, with <see cref="IContinuationCache.TryShortenExpiryAsync"/>, to at most
/// <see cref="PredecessorGrace"/>: the predecessor keeps answering retries of a lost response within the grace, the
/// shortening never lengthens or recreates an entry, and every entry left behind by a failure expires at its own
/// deadline.
/// </para>
/// <para>
/// <b>Failures.</b> Only <see cref="ContinuationCacheUnavailableException"/> and the expiry of
/// <see cref="StoreTimeout"/> are translated, to <c>StoreUnavailable</c>; on the cached write alone,
/// <see cref="ContinuationCacheEntryExpiredException"/> is translated to <c>Gone</c>; caller cancellation propagates;
/// any other store exception is a provider defect and propagates. The timeout signals cancellation through the
/// operation's token and is never assumed to abandon the operation; it bounds a store call's duration only for a
/// provider that meets the contract's prompt-cancellation requirement. For robustness against one that does not, a
/// store success that completes after caller cancellation propagates cancellation, one that completes after the
/// timeout is <c>StoreUnavailable</c>, and a reported unavailability is <c>StoreUnavailable</c> even if the caller has
/// meanwhile canceled. A write confirmed only after the new token's deadline is <c>Gone</c>. Nothing from a token,
/// value, or store failure is logged or carried in an outcome.
/// </para>
/// <para>
/// Thread-safe. Instances sharing a key ring, application discriminator, and store redeem each other's tokens.
/// </para>
/// </remarks>
internal sealed class ContinuationTokenService
{
    /// <summary>The prefix that distinguishes a cached reference from an inline token; part of format v1.</summary>
    public const string ReferencePrefix = "r1.";

    /// <summary>The Data Protection purpose of references; part of format v1.</summary>
    public const string ReferencePurpose = "Ignixa.Federation.Continuations.Reference.v1";

    /// <summary>The Data Protection purpose of cached values; the handle id is a subpurpose. Part of format v1.</summary>
    public const string ValuePurpose = "Ignixa.Federation.Continuations.Cached.v1";

    /// <summary>The prefix of every store key this type writes.</summary>
    public const string CacheKeyPrefix = "Ignixa.Federation.Continuations.v1:";

    /// <summary>The default largest cached envelope, in plaintext bytes.</summary>
    public const int DefaultMaxCachedPlaintextBytes = 256 * 1024;

    /// <summary>
    /// The longest presented reference, in characters, that is decoded and unprotected: the prefix (3) plus the
    /// Base64Url length of the reference plaintext and the Data Protection allowance.
    /// </summary>
    public const int MaxReferenceLength = 3 + (((MaxProtectedReferenceBytes * 4) + 2) / 3);

    private const int HandleBytes = 32;

    // Envelope header (version, four instants, binding digest, snapshot length: 69) + empty dependency count (2) + handle.
    private const int ReferencePlaintextBytes = 71 + HandleBytes;
    private const int MaxProtectedReferenceBytes = ReferencePlaintextBytes + ContinuationProtector.ProtectionOverheadAllowance;

    /// <summary>The longest a cached predecessor stays redeemable once a successor of it has been issued.</summary>
    public static readonly TimeSpan PredecessorGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long one store operation runs before its token is canceled and its outcome treated as unavailable. The
    /// timeout only signals cancellation; it bounds the call's duration only as far as a conforming provider's bounded
    /// cancellation latency does, and the operation may still take effect.
    /// </summary>
    public static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(5);

    private static readonly ContinuationDependencySet NoDependencies = ContinuationDependencySet.Create([], []);

    private readonly ContinuationProtector _inline;
    private readonly IDataProtector _referenceProtector;
    private readonly IDataProtector _valueProtector;
    private readonly TimeProvider _time;
    private readonly IContinuationCache _cache;
    private readonly int _maxCachedPlaintextBytes;
    private readonly int _maxValueBytes;
    private readonly long _slidingTicks;
    private readonly long _absoluteTicks;

    /// <summary>Initializes a new instance of the <see cref="ContinuationTokenService"/> class.</summary>
    /// <param name="provider">The Data Protection provider; its key ring and application discriminator decide which instances interoperate.</param>
    /// <param name="options">The published token options; read once, here.</param>
    /// <param name="time">The clock.</param>
    /// <param name="cache">The store for cached values; it must hold a value of the largest protected envelope.</param>
    /// <param name="maxCachedPlaintextBytes">The largest cached envelope, in plaintext bytes; not below the inline limit.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The options are invalid for <see cref="ContinuationProtector"/>, the cached limit is below the inline limit or
    /// its ciphertext bound is not representable, or the store's <see cref="IContinuationCache.MaxValueBytes"/> is
    /// below that bound.
    /// </exception>
    public ContinuationTokenService(
        IDataProtectionProvider provider,
        TokenOptions options,
        TimeProvider time,
        IContinuationCache cache,
        int maxCachedPlaintextBytes = DefaultMaxCachedPlaintextBytes)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _inline = new ContinuationProtector(provider, options, time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCachedPlaintextBytes, options.MaxInlineTokenBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxCachedPlaintextBytes, Array.MaxLength - ContinuationProtector.ProtectionOverheadAllowance);
        int maxValueBytes = maxCachedPlaintextBytes + ContinuationProtector.ProtectionOverheadAllowance;
        ArgumentOutOfRangeException.ThrowIfLessThan(cache.MaxValueBytes, maxValueBytes, nameof(cache));
        Debug.Assert(ContinuationEnvelope.Measure(NoDependencies, HandleBytes) == ReferencePlaintextBytes, "Reference layout drifted from the envelope.");

        _referenceProtector = provider.CreateProtector(ReferencePurpose);
        _valueProtector = provider.CreateProtector(ValuePurpose);
        _time = time;
        _cache = cache;
        _maxCachedPlaintextBytes = maxCachedPlaintextBytes;
        _maxValueBytes = maxValueBytes;
        _slidingTicks = options.SlidingLifetime.Ticks;
        _absoluteTicks = options.AbsoluteLifetime.Ticks;
    }

    /// <summary>Issues the first token of a chain, inline when it fits and as a cached reference otherwise.</summary>
    /// <param name="caller">The caller the chain is bound to.</param>
    /// <param name="dependencies">Exactly the shards the streams read and the definitions the query resolved, with their planned revisions.</param>
    /// <param name="snapshot">Trusted internal plan and replay bytes.</param>
    /// <param name="cancellationToken">Cancels the store write; cancellation propagates.</param>
    /// <returns>
    /// The token; <see cref="ContinuationIssuanceOutcome.TooLarge"/> over the cached limit;
    /// <see cref="ContinuationIssuanceOutcome.Gone"/> when no deadline after now is representable; or
    /// <see cref="ContinuationIssuanceOutcome.StoreUnavailable"/> when the write failed or its outcome is unknown.
    /// </returns>
    public async Task<ContinuationIssuance> IssueAsync(
        ContinuationBinding caller,
        ContinuationDependencySet dependencies,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken)
    {
        ContinuationIssue inline = _inline.Issue(caller, dependencies, snapshot.Span);
        if (!inline.NeedsCachedReference)
        {
            return ContinuationIssuance.Issued(inline.InlineToken, inline.DeadlineTicks);
        }

        long now = _time.GetUtcNow().UtcTicks;
        return await IssueCachedAsync(now, now, SaturatingAdd(now, _absoluteTicks), caller, dependencies, snapshot, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Issues the next token of a chain from a redeemed one, carrying its binding, dependencies, and chain timing. When
    /// the predecessor is a cached reference, its entry is shortened to <see cref="PredecessorGrace"/> once the
    /// successor is durable; the shortening is not abandoned on caller cancellation.
    /// </summary>
    /// <param name="predecessor">The redeemed continuation the next page was produced from.</param>
    /// <param name="snapshot">Trusted internal plan and replay bytes for the next page.</param>
    /// <param name="cancellationToken">Cancels until the successor is durable; cancellation propagates.</param>
    /// <returns>
    /// The successor; <see cref="ContinuationIssuanceOutcome.TooLarge"/> over the cached limit;
    /// <see cref="ContinuationIssuanceOutcome.Gone"/> when the predecessor is no longer live or the successor expires
    /// during the store operations; or <see cref="ContinuationIssuanceOutcome.StoreUnavailable"/> when a write or
    /// shortening failed or its outcome is unknown. An unpublished successor entry expires at its own deadline.
    /// </returns>
    public async Task<ContinuationIssuance> IssueSuccessorAsync(
        RedeemedContinuation predecessor,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predecessor);

        if (_time.GetUtcNow().UtcTicks >= predecessor.DeadlineTicks)
        {
            return ContinuationIssuance.Gone;
        }

        ContinuationIssue inline = _inline.IssueSuccessor(predecessor, snapshot.Span);
        ContinuationIssuance successor;
        if (inline.NeedsCachedReference)
        {
            // As for inline successors, a clock behind the chain start yields a not-yet-valid token, never a malformed one.
            long now = Math.Max(_time.GetUtcNow().UtcTicks, predecessor.ChainIssuedTicks);
            successor = await IssueCachedAsync(predecessor.ChainIssuedTicks, now, predecessor.ChainDeadlineTicks, predecessor.Binding,
                predecessor.Dependencies, snapshot, cancellationToken).ConfigureAwait(false);
            if (!successor.IsIssued)
            {
                return successor;
            }
        }
        else
        {
            successor = ContinuationIssuance.Issued(inline.InlineToken, inline.DeadlineTicks);
        }

        if (predecessor.CacheKey is null)
        {
            return successor;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_time.GetUtcNow().UtcTicks >= predecessor.DeadlineTicks)
        {
            return ContinuationIssuance.Gone;
        }

        // Once started, the shortening runs to completion or timeout regardless of the caller: abandoning it would
        // leave a durable successor beside a predecessor that keeps its full lifetime.
        (bool available, bool live) = await CallStoreAsync(
            token => _cache.TryShortenExpiryAsync(predecessor.CacheKey, PredecessorGrace, token), CancellationToken.None).ConfigureAwait(false);
        if (!available)
        {
            return ContinuationIssuance.StoreUnavailable;
        }

        // A confirmed clamp stays applied, but its acknowledgement may arrive after the successor expired.
        return live && _time.GetUtcNow().UtcTicks < successor.DeadlineTicks ? successor
            : ContinuationIssuance.Gone;
    }

    /// <summary>Redeems a presented inline token or cached reference for a caller.</summary>
    /// <param name="token">The presented token, or <see langword="null"/> when none was presented.</param>
    /// <param name="caller">The caller presenting it.</param>
    /// <param name="currentRevision">
    /// Trusted lookup of the current revision for a dependency group and key, or <see langword="null"/> when it no
    /// longer exists. Called only for an authentic, live continuation bound to <paramref name="caller"/>, and only for
    /// its own dependencies. Exceptions it throws propagate.
    /// </param>
    /// <param name="cancellationToken">Cancels the store read; cancellation propagates.</param>
    /// <returns>
    /// The redeemed continuation, or a refusal that carries nothing from the token or store:
    /// <see cref="ContinuationRedemptionOutcome.StoreUnavailable"/> when the read failed or its outcome is unknown.
    /// Redemption never writes, refreshes, or shortens an entry.
    /// </returns>
    public async Task<ContinuationRedemption> RedeemAsync(
        string? token,
        ContinuationBinding caller,
        Func<ContinuationDependencyGroup, string, string?> currentRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(currentRevision);

        if (token is null || !token.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return _inline.Redeem(token, caller, currentRevision);
        }

        ContinuationRedemption? refusal = OpenReference(token.AsSpan(ReferencePrefix.Length), caller, out Handle? handle);
        if (refusal is not null)
        {
            return refusal;
        }

        Debug.Assert(handle is not null, "An accepted reference yields its handle.");
        (bool available, byte[]? stored) = await CallStoreAsync(t => _cache.GetAsync(handle.Key, t), cancellationToken).ConfigureAwait(false);
        return !available ? ContinuationRedemption.StoreUnavailable
            : stored is null ? ContinuationRedemption.Gone
            : OpenValue(stored, handle, caller, currentRevision);
    }

    private static long SaturatingAdd(long ticks, long duration) =>
        ticks > long.MaxValue - duration ? long.MaxValue : ticks + duration;

    private static string KeyOf(ReadOnlySpan<byte> id) => CacheKeyPrefix + Base64Url.EncodeToString(SHA256.HashData(id));

    private static byte[] Envelope(
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        long slidingDeadlineTicks,
        ContinuationBinding binding,
        ContinuationDependencySet dependencies,
        ReadOnlySpan<byte> snapshot)
    {
        byte[] plaintext = new byte[ContinuationEnvelope.Measure(dependencies, snapshot.Length)];
        ContinuationEnvelope.Write(plaintext, chainIssuedTicks, issuedTicks, chainDeadlineTicks, slidingDeadlineTicks, binding.Hash, dependencies, snapshot);
        return plaintext;
    }

    private async Task<ContinuationIssuance> IssueCachedAsync(
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        ContinuationBinding binding,
        ContinuationDependencySet dependencies,
        ReadOnlyMemory<byte> snapshot,
        CancellationToken cancellationToken)
    {
        ContinuationIssuance? refusal = Prepare(chainIssuedTicks, issuedTicks, chainDeadlineTicks, binding, dependencies, snapshot.Span, out Entry? entry);
        if (refusal is not null)
        {
            return refusal;
        }

        Debug.Assert(entry is not null, "A prepared issue yields its entry.");
        cancellationToken.ThrowIfCancellationRequested();
        var options = new DistributedCacheEntryOptions { AbsoluteExpiration = entry.Expiry };
        (bool available, bool stored) = await CallStoreAsync(
            async t =>
            {
                try
                {
                    await _cache.SetAsync(entry.Key, entry.Value, options, t).ConfigureAwait(false);
                    return true;
                }
                catch (ContinuationCacheEntryExpiredException)
                {
                    // The store's clock had already passed the deadline: nothing is stored, the token is dead.
                    return false;
                }
            },
            cancellationToken).ConfigureAwait(false);
        if (!available)
        {
            return ContinuationIssuance.StoreUnavailable;
        }

        // A write refused as expired, or confirmed only after the token's own deadline, is never handed out; a
        // confirmed entry expires on its own.
        return stored && _time.GetUtcNow().UtcTicks < entry.Expiry.UtcTicks
            ? ContinuationIssuance.Issued(entry.Reference, entry.Expiry.UtcTicks)
            : ContinuationIssuance.Gone;
    }

    private ContinuationIssuance? Prepare(
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        ContinuationBinding binding,
        ContinuationDependencySet dependencies,
        ReadOnlySpan<byte> snapshot,
        out Entry? entry)
    {
        entry = null;
        if (ContinuationEnvelope.Measure(dependencies, snapshot.Length) > _maxCachedPlaintextBytes)
        {
            return ContinuationIssuance.TooLarge;
        }

        long slidingDeadlineTicks = SaturatingAdd(issuedTicks, _slidingTicks);
        long expiryTicks = Math.Min(Math.Min(chainDeadlineTicks, slidingDeadlineTicks), DateTimeOffset.MaxValue.UtcTicks);
        if (expiryTicks <= issuedTicks)
        {
            return ContinuationIssuance.Gone;
        }

        byte[] id = RandomNumberGenerator.GetBytes(HandleBytes);
        byte[] value = _valueProtector.CreateProtector(Base64Url.EncodeToString(id)).Protect(
            Envelope(chainIssuedTicks, issuedTicks, chainDeadlineTicks, slidingDeadlineTicks, binding, dependencies, snapshot));
        byte[] reference = _referenceProtector.Protect(
            Envelope(chainIssuedTicks, issuedTicks, chainDeadlineTicks, slidingDeadlineTicks, binding, NoDependencies, id));
        if (value.Length > _maxValueBytes || reference.Length > MaxProtectedReferenceBytes)
        {
            throw new InvalidOperationException("The configured data protection overhead exceeds the continuation token allowance.");
        }

        entry = new Entry(KeyOf(id), value, new DateTimeOffset(expiryTicks, TimeSpan.Zero), ReferencePrefix + Base64Url.EncodeToString(reference));
        return null;
    }

    private ContinuationRedemption? OpenReference(ReadOnlySpan<char> body, ContinuationBinding caller, out Handle? handle)
    {
        handle = null;
        if (!ContinuationProtector.TryDecode(body, MaxReferenceLength - ReferencePrefix.Length, out byte[]? protectedBytes))
        {
            return ContinuationRedemption.Invalid;
        }

        byte[] contents;
        try
        {
            contents = _referenceProtector.Unprotect(protectedBytes);
        }
        catch (CryptographicException)
        {
            // Wrong key ring, discriminator, or purpose, or tampered bytes. Deliberately not logged or surfaced.
            return ContinuationRedemption.Invalid;
        }

        if (!ContinuationEnvelope.TryRead(contents, out ContinuationEnvelope envelope)
            || envelope.Dependencies.EncodedLength != NoDependencies.EncodedLength
            || envelope.Snapshot.Length != HandleBytes)
        {
            return ContinuationRedemption.Invalid;
        }

        if (!caller.Matches(envelope.BindingHash))
        {
            return ContinuationRedemption.CallerMismatch;
        }

        if (!IsWithin(envelope.IssuedTicks, envelope.DeadlineTicks))
        {
            return ContinuationRedemption.Gone;
        }

        byte[] id = envelope.Snapshot.ToArray();
        handle = new Handle(id, KeyOf(id), envelope.ChainIssuedTicks, envelope.IssuedTicks, envelope.ChainDeadlineTicks, envelope.DeadlineTicks);
        return null;
    }

    private ContinuationRedemption OpenValue(
        byte[] stored,
        Handle handle,
        ContinuationBinding caller,
        Func<ContinuationDependencyGroup, string, string?> currentRevision)
    {
        if (stored.Length > _maxValueBytes)
        {
            return ContinuationRedemption.Invalid;
        }

        byte[] contents;
        try
        {
            contents = _valueProtector.CreateProtector(Base64Url.EncodeToString(handle.Id)).Unprotect(stored);
        }
        catch (CryptographicException)
        {
            // Another handle's value, a foreign key ring, or corrupted bytes. Deliberately not logged or surfaced.
            return ContinuationRedemption.Invalid;
        }

        if (contents.Length > _maxCachedPlaintextBytes
            || !ContinuationEnvelope.TryRead(contents, out ContinuationEnvelope envelope)
            || envelope.ChainIssuedTicks != handle.ChainIssuedTicks
            || envelope.IssuedTicks != handle.IssuedTicks
            || envelope.ChainDeadlineTicks != handle.ChainDeadlineTicks
            || envelope.DeadlineTicks != handle.DeadlineTicks
            || !caller.Matches(envelope.BindingHash))
        {
            return ContinuationRedemption.Invalid;
        }

        // The value is accepted as of the successful read; the window is re-checked after it, never widened by it.
        if (!IsWithin(handle.IssuedTicks, handle.DeadlineTicks) || !envelope.Dependencies.AreCurrent(currentRevision))
        {
            return ContinuationRedemption.Gone;
        }

        return ContinuationRedemption.Redeemed(new RedeemedContinuation(
            caller, envelope.Dependencies, handle.ChainIssuedTicks, handle.ChainDeadlineTicks, handle.DeadlineTicks, handle.Key, [.. envelope.Snapshot]));
    }

    private bool IsWithin(long issuedTicks, long deadlineTicks)
    {
        long now = _time.GetUtcNow().UtcTicks;
        return now >= issuedTicks && now < deadlineTicks;
    }

    /// <summary>
    /// Runs one store operation under <see cref="StoreTimeout"/>. Cancellation is only signaled: the contract requires
    /// the provider to stop promptly, but nothing assumes it did, so a success that completes after the caller canceled
    /// propagates cancellation and one that completes after the timeout is unavailable; its effect may still have been
    /// applied. Only the contract's availability failure (whatever the caller's cancellation state) and the timeout are
    /// translated, to unavailable; caller cancellation and any other exception propagate.
    /// </summary>
    private async Task<(bool Available, T Result)> CallStoreAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(StoreTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        T result;
        try
        {
            result = await operation(linked.Token).ConfigureAwait(false);
        }
        catch (ContinuationCacheUnavailableException)
        {
            return (false, default!);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return (false, default!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return timeout.IsCancellationRequested ? (false, default!) : (true, result);
    }

    /// <summary>A prepared cached entry. The default <see cref="object.ToString"/> is kept, so nothing is rendered.</summary>
    private sealed class Entry(string key, byte[] value, DateTimeOffset expiry, string reference)
    {
        public string Key { get; } = key;

        public byte[] Value { get; } = value;

        public DateTimeOffset Expiry { get; } = expiry;

        public string Reference { get; } = reference;
    }

    /// <summary>An authenticated, live reference. The default <see cref="object.ToString"/> is kept, so nothing is rendered.</summary>
    private sealed class Handle(byte[] id, string key, long chainIssuedTicks, long issuedTicks, long chainDeadlineTicks, long deadlineTicks)
    {
        public byte[] Id { get; } = id;

        public string Key { get; } = key;

        public long ChainIssuedTicks { get; } = chainIssuedTicks;

        public long IssuedTicks { get; } = issuedTicks;

        public long ChainDeadlineTicks { get; } = chainDeadlineTicks;

        public long DeadlineTicks { get; } = deadlineTicks;
    }
}

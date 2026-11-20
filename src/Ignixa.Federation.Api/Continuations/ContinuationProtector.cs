using System.Buffers;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Ignixa.Federation.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// Issues and redeems inline continuation tokens (design spec §7.4): a format v1 <see cref="ContinuationEnvelope"/>
/// protected by ASP.NET Core Data Protection under <see cref="Purpose"/>, then Base64Url without padding.
/// Transport-independent; it has no HTTP, claims, or logging dependencies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Snapshot ingress.</b> The snapshot is trusted internal plan and replay data produced by the federation itself
/// (cursor positions, scope, the resolved query). It must never contain resource or shard response bodies, bearer
/// credentials, or the raw inbound token. Typed snapshot serialization, the distributed cache for oversize
/// snapshots, the continuation route, and the per-page role recheck are separate components; this type only reports
/// <see cref="ContinuationIssue.NeedsCachedReference"/> when a snapshot does not fit inline.
/// </para>
/// <para>
/// <b>Bounds.</b> <see cref="TokenOptions.MaxInlineTokenBytes"/> limits the plaintext, envelope included; a snapshot
/// that would exceed it gets no token and Data Protection is not called. Presented tokens are bounded before any
/// decoding: the protected form may be at most the plaintext limit plus <see cref="ProtectionOverheadAllowance"/>
/// bytes, which covers the default AES-256-CBC with HMACSHA256 packaging (magic, key id, key modifier, IV, padding,
/// MAC: 85 to 100 bytes) and the stronger HMACSHA512 and GCM configurations, so <see cref="MaxTokenLength"/> is
/// that byte count in unpadded Base64Url, ceil(4n/3): 3072 characters for the default 2048-byte limit. Issuance
/// fails loudly if a configured algorithm ever exceeds the allowance, rather than emitting a token this instance
/// would refuse.
/// </para>
/// <para>
/// <b>Redemption order.</b> Checks run so that nothing about a token is disclosed on failure and unauthenticated
/// input never reaches revision data: an absent token (<see langword="null"/>) is <see cref="ContinuationRedemptionOutcome.Gone"/>,
/// because there is nothing to resume; any presented text that is blank, too long, not canonical unpadded
/// Base64Url, not authentic under this key ring and purpose, over the plaintext limit, or not a well-formed v1
/// envelope is <see cref="ContinuationRedemptionOutcome.Invalid"/>; then a different caller binding is
/// <see cref="ContinuationRedemptionOutcome.CallerMismatch"/>; then a token outside its window or whose relevant
/// dependencies changed or disappeared is <see cref="ContinuationRedemptionOutcome.Gone"/>.
/// </para>
/// <para>
/// <b>Lifetime.</b> A token is valid in <c>[issued, min(sliding deadline, chain deadline))</c>. Issuance sets deadlines
/// from the published <see cref="TokenOptions"/>; redemption uses those authenticated deadlines, not local options.
/// A successor sets a new sliding deadline but keeps the original chain deadline unchanged. Redemption
/// never writes or mutates state; a predecessor stays redeemable within its own window after a successor is issued,
/// and repeat redemption returns the same snapshot.
/// </para>
/// <para>
/// Thread-safe: the options are copied at construction and the Data Protection protector is thread-safe.
/// Instances sharing a key ring and application discriminator redeem each other's tokens.
/// </para>
/// </remarks>
internal sealed class ContinuationProtector
{
    /// <summary>The Data Protection purpose; part of format v1.</summary>
    public const string Purpose = "Ignixa.Federation.Continuations.Inline.v1";

    /// <summary>The bytes Data Protection packaging may add to the plaintext, used to bound presented tokens.</summary>
    public const int ProtectionOverheadAllowance = 256;

    private static readonly SearchValues<char> Base64UrlAlphabet =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly int _maxPlaintextBytes;
    private readonly int _maxProtectedBytes;
    private readonly long _slidingTicks;
    private readonly long _absoluteTicks;

    /// <summary>Initializes a new instance of the <see cref="ContinuationProtector"/> class.</summary>
    /// <param name="provider">The Data Protection provider; its key ring and application discriminator decide which instances interoperate.</param>
    /// <param name="options">The published token options; read once, here.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit or lifetime is not positive, or the derived token bound is not representable.</exception>
    public ContinuationProtector(IDataProtectionProvider provider, TokenOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxInlineTokenBytes, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.SlidingLifetime, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AbsoluteLifetime, TimeSpan.Zero, nameof(options));

        long maxProtectedBytes = (long)options.MaxInlineTokenBytes + ProtectionOverheadAllowance;
        long maxTokenLength = ((maxProtectedBytes * 4) + 2) / 3;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxTokenLength, Array.MaxLength, nameof(options));

        _protector = provider.CreateProtector(Purpose);
        _time = time;
        _maxPlaintextBytes = options.MaxInlineTokenBytes;
        _maxProtectedBytes = (int)maxProtectedBytes;
        _slidingTicks = options.SlidingLifetime.Ticks;
        _absoluteTicks = options.AbsoluteLifetime.Ticks;
        MaxTokenLength = (int)maxTokenLength;
    }

    /// <summary>Gets the longest presented token, in characters, that is decoded and unprotected; longer input is invalid.</summary>
    public int MaxTokenLength { get; }

    /// <summary>Issues the first token of a chain.</summary>
    /// <param name="caller">The caller the chain is bound to.</param>
    /// <param name="dependencies">Exactly the shards the streams read and the definitions the query resolved, with their planned revisions.</param>
    /// <param name="snapshot">Trusted internal plan and replay bytes (see remarks on the type).</param>
    /// <returns>An inline token, or <see cref="ContinuationIssue.NeedsCachedReference"/> when the snapshot does not fit.</returns>
    public ContinuationIssue Issue(ContinuationBinding caller, ContinuationDependencySet dependencies, ReadOnlySpan<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(dependencies);

        long now = _time.GetUtcNow().UtcTicks;
        return Protect(now, now, SaturatingAdd(now, _absoluteTicks), caller.Hash, dependencies, snapshot);
    }

    /// <summary>
    /// Issues the next token of a chain from a redeemed one: same caller binding, dependencies, and chain issue
    /// instant; a new issue instant and snapshot.
    /// </summary>
    /// <param name="predecessor">The redeemed continuation the next page was produced from.</param>
    /// <param name="snapshot">Trusted internal plan and replay bytes for the next page.</param>
    /// <returns>An inline token, or <see cref="ContinuationIssue.NeedsCachedReference"/> when the snapshot does not fit.</returns>
    public ContinuationIssue IssueSuccessor(RedeemedContinuation predecessor, ReadOnlySpan<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(predecessor);

        // A clock that stepped back behind the chain start (e.g. skew between instances) must not produce an
        // envelope with issued < chain, which would read back as malformed; such a token is simply not yet valid.
        long now = Math.Max(_time.GetUtcNow().UtcTicks, predecessor.ChainIssuedTicks);
        return Protect(predecessor.ChainIssuedTicks, now, predecessor.ChainDeadlineTicks, predecessor.Binding.Hash, predecessor.Dependencies, snapshot);
    }

    /// <summary>Redeems a presented token for a caller.</summary>
    /// <param name="token">The presented token, or <see langword="null"/> when none was presented.</param>
    /// <param name="caller">The caller presenting it.</param>
    /// <param name="currentRevision">
    /// Trusted lookup of the current revision for a dependency group and key, or <see langword="null"/> when it no
    /// longer exists. Called only for an authentic, live token bound to <paramref name="caller"/>, and only for its
    /// own dependencies. Exceptions it throws propagate.
    /// </param>
    /// <returns>The redeemed continuation, or a refusal that carries nothing from the token.</returns>
    public ContinuationRedemption Redeem(string? token, ContinuationBinding caller, Func<ContinuationDependencyGroup, string, string?> currentRevision)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(currentRevision);

        if (token is null)
        {
            return ContinuationRedemption.Gone;
        }

        if (!TryDecode(token, out byte[]? protectedBytes))
        {
            return ContinuationRedemption.Invalid;
        }

        byte[] contents;
        try
        {
            contents = _protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException)
        {
            // Wrong key ring, discriminator, or purpose, or tampered bytes. Deliberately not logged or surfaced.
            return ContinuationRedemption.Invalid;
        }

        if (contents.Length > _maxPlaintextBytes || !ContinuationEnvelope.TryRead(contents, out ContinuationEnvelope envelope))
        {
            return ContinuationRedemption.Invalid;
        }

        if (!caller.Matches(envelope.BindingHash))
        {
            return ContinuationRedemption.CallerMismatch;
        }

        long now = _time.GetUtcNow().UtcTicks;
        if (now < envelope.IssuedTicks || now >= envelope.DeadlineTicks || !envelope.Dependencies.AreCurrent(currentRevision))
        {
            return ContinuationRedemption.Gone;
        }

        return ContinuationRedemption.Redeemed(
            new RedeemedContinuation(caller, envelope.Dependencies, envelope.ChainIssuedTicks, envelope.ChainDeadlineTicks, [.. envelope.Snapshot]));
    }

    private static long SaturatingAdd(long ticks, long duration) =>
        ticks > long.MaxValue - duration ? long.MaxValue : ticks + duration;

    private ContinuationIssue Protect(
        long chainIssuedTicks,
        long issuedTicks,
        long chainDeadlineTicks,
        ReadOnlySpan<byte> bindingHash,
        ContinuationDependencySet dependencies,
        ReadOnlySpan<byte> snapshot)
    {
        long length = ContinuationEnvelope.Measure(dependencies, snapshot.Length);
        if (length > _maxPlaintextBytes)
        {
            return ContinuationIssue.NeedsCache;
        }

        byte[] plaintext = new byte[length];
        ContinuationEnvelope.Write(plaintext, chainIssuedTicks, issuedTicks, chainDeadlineTicks,
            SaturatingAdd(issuedTicks, _slidingTicks), bindingHash, dependencies, snapshot);
        byte[] protectedBytes = _protector.Protect(plaintext);
        if (protectedBytes.Length > _maxProtectedBytes)
        {
            throw new InvalidOperationException("The configured data protection overhead exceeds the continuation token allowance.");
        }

        return ContinuationIssue.Inline(Base64Url.EncodeToString(protectedBytes));
    }

    // Accepts only canonical unpadded Base64Url within MaxTokenLength: the alphabet check refuses padding,
    // whitespace, and other alphabets, and the decoder refuses length % 4 == 1 and non-zero unused trailing bits, so
    // each protected payload has exactly one accepted spelling.
    private bool TryDecode(string token, [NotNullWhen(true)] out byte[]? protectedBytes)
    {
        protectedBytes = null;
        if (token.Length == 0 || token.Length > MaxTokenLength || token.AsSpan().ContainsAnyExcept(Base64UrlAlphabet))
        {
            return false;
        }

        byte[] decoded = new byte[Base64Url.GetMaxDecodedLength(token.Length)];
        if (Base64Url.DecodeFromChars(token, decoded, out _, out int written) != OperationStatus.Done)
        {
            return false;
        }

        protectedBytes = written == decoded.Length ? decoded : decoded[..written];
        return true;
    }
}

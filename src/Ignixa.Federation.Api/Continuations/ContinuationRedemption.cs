using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The result of redeeming a presented continuation: the redeemed state, or the reason it was refused. A refusal
/// carries nothing from the token. The default <see cref="object.ToString"/> is kept, so no content is rendered.
/// </summary>
internal sealed class ContinuationRedemption
{
    /// <summary>The invalid (400) refusal.</summary>
    internal static readonly ContinuationRedemption Invalid = new(ContinuationRedemptionOutcome.Invalid, null);

    /// <summary>The caller mismatch (403) refusal.</summary>
    internal static readonly ContinuationRedemption CallerMismatch = new(ContinuationRedemptionOutcome.CallerMismatch, null);

    /// <summary>The gone (410) refusal.</summary>
    internal static readonly ContinuationRedemption Gone = new(ContinuationRedemptionOutcome.Gone, null);

    /// <summary>The store unavailable (503) refusal.</summary>
    internal static readonly ContinuationRedemption StoreUnavailable = new(ContinuationRedemptionOutcome.StoreUnavailable, null);

    private ContinuationRedemption(ContinuationRedemptionOutcome outcome, RedeemedContinuation? continuation)
    {
        Outcome = outcome;
        Continuation = continuation;
    }

    /// <summary>Gets why the redemption succeeded or failed.</summary>
    public ContinuationRedemptionOutcome Outcome { get; }

    /// <summary>Gets the redeemed state when <see cref="Outcome"/> is <see cref="ContinuationRedemptionOutcome.Redeemed"/>, otherwise <see langword="null"/>.</summary>
    public RedeemedContinuation? Continuation { get; }

    /// <summary>Gets whether the continuation was redeemed.</summary>
    [MemberNotNullWhen(true, nameof(Continuation))]
    public bool IsRedeemed => Continuation is not null;

    /// <summary>Wraps a successfully redeemed continuation.</summary>
    /// <param name="continuation">The redeemed state.</param>
    /// <returns>A redeemed result.</returns>
    internal static ContinuationRedemption Redeemed(RedeemedContinuation continuation) =>
        new(ContinuationRedemptionOutcome.Redeemed, continuation);
}

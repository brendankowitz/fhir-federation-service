using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The result of issuing a continuation: the token, or the reason none was issued. The default
/// <see cref="object.ToString"/> is kept, so the token is never rendered.
/// </summary>
internal sealed class ContinuationIssuance
{
    /// <summary>The refusal for a snapshot over the cached plaintext limit.</summary>
    internal static readonly ContinuationIssuance TooLarge = new(ContinuationIssuanceOutcome.TooLarge, null, 0);

    /// <summary>The refusal for a chain that cannot continue.</summary>
    internal static readonly ContinuationIssuance Gone = new(ContinuationIssuanceOutcome.Gone, null, 0);

    /// <summary>The refusal for an unavailable store or an unknown store outcome.</summary>
    internal static readonly ContinuationIssuance StoreUnavailable = new(ContinuationIssuanceOutcome.StoreUnavailable, null, 0);

    private ContinuationIssuance(ContinuationIssuanceOutcome outcome, string? token, long deadlineTicks)
    {
        Outcome = outcome;
        Token = token;
        DeadlineTicks = deadlineTicks;
    }

    /// <summary>Gets why a token was or was not issued.</summary>
    public ContinuationIssuanceOutcome Outcome { get; }

    /// <summary>Gets the token when <see cref="Outcome"/> is <see cref="ContinuationIssuanceOutcome.Issued"/>, otherwise <see langword="null"/>.</summary>
    public string? Token { get; }

    /// <summary>Gets the exclusive UTC deadline of the issued token, or zero when none was issued.</summary>
    internal long DeadlineTicks { get; }

    /// <summary>Gets whether a token was issued.</summary>
    [MemberNotNullWhen(true, nameof(Token))]
    public bool IsIssued => Token is not null;

    /// <summary>Wraps an issued token.</summary>
    /// <param name="token">The inline token or cached reference.</param>
    /// <param name="deadlineTicks">The token's exclusive UTC deadline.</param>
    /// <returns>An issued result.</returns>
    internal static ContinuationIssuance Issued(string token, long deadlineTicks) => new(ContinuationIssuanceOutcome.Issued, token, deadlineTicks);
}

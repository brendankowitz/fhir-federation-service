using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The result of issuing a continuation: an inline token, or the explicit outcome that the serialized snapshot is
/// over <c>MaxInlineTokenBytes</c> and needs a cached reference (design spec §7.4). No token is produced in that
/// case; storing the snapshot and issuing a reference token is a separate component. The default
/// <see cref="object.ToString"/> is kept, so the token is never rendered.
/// </summary>
internal sealed class ContinuationIssue
{
    /// <summary>The outcome for a snapshot over the inline limit.</summary>
    internal static readonly ContinuationIssue NeedsCache = new(null, 0);

    private ContinuationIssue(string? inlineToken, long deadlineTicks)
    {
        InlineToken = inlineToken;
        DeadlineTicks = deadlineTicks;
    }

    /// <summary>Gets the inline token, Base64Url without padding, or <see langword="null"/> when a cached reference is needed.</summary>
    public string? InlineToken { get; }

    /// <summary>Gets the exclusive UTC deadline encoded in the token, or zero when no token was issued.</summary>
    internal long DeadlineTicks { get; }

    /// <summary>Gets whether the snapshot is too large to travel inline and needs a cached reference instead.</summary>
    [MemberNotNullWhen(false, nameof(InlineToken))]
    public bool NeedsCachedReference => InlineToken is null;

    /// <summary>Wraps an inline token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="deadlineTicks">The exclusive UTC deadline encoded in the token.</param>
    /// <returns>An inline result.</returns>
    internal static ContinuationIssue Inline(string token, long deadlineTicks) => new(token, deadlineTicks);
}

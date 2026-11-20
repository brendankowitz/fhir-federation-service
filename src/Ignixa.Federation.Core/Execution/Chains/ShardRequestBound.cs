namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// The operator-trusted transport ceilings for one shard (design spec §7.5): the largest GET request-target the shard is
/// known to accept, and, only for a shard whose profile supports POST search, the largest POST form body.
/// <see cref="ForwardChainResolver"/> rejects a bound set that does not match the profile rather than assume a default.
/// </summary>
/// <remarks>
/// <para>
/// A shard that supports POST search needs both ceilings: its witness searches and ordinary primary searches go by POST
/// and are measured against <see cref="MaxPostBodyBytes"/>, but a primary search that repeats an equal decoded pair goes by
/// GET, because a POST backend may collapse equal pairs, and is measured against <see cref="MaxGetTargetBytes"/>. A GET-only
/// shard has only the GET ceiling.
/// </para>
/// <para>
/// <see cref="ForwardChainResolver"/> measures each complete initial request in UTF-8 bytes before sending or exposing it.
/// A POST is the form-encoded body. A GET is the escaped request-target (<c>System.Uri</c> path and query) of the shard's
/// canonical base path plus <c>/Type?</c> and every pair. Both include every separator and the largest <c>_count</c> the
/// consuming executor can send. Headers, the scheme and the host are not counted, so a bound must leave room for them. A
/// request over its bound is refused rather than split. A bound is a configured fact, never inferred from a backend
/// profile, and it is not enforcement on the wire.
/// </para>
/// </remarks>
public sealed class ShardRequestBound
{
    /// <summary>Initializes a new instance of the <see cref="ShardRequestBound"/> class.</summary>
    /// <param name="maxGetTargetBytes">The largest measured GET request-target, in bytes; positive.</param>
    /// <param name="maxPostBodyBytes">
    /// The largest measured POST form body, in bytes; positive when given. Give it exactly for a shard whose profile
    /// supports POST search; <see cref="ForwardChainResolver"/> rejects a bound for any other shard.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxGetTargetBytes"/> is not positive, or <paramref name="maxPostBodyBytes"/> is given and not positive.
    /// </exception>
    public ShardRequestBound(int maxGetTargetBytes, int? maxPostBodyBytes = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGetTargetBytes);
        if (maxPostBodyBytes is { } body)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(body, nameof(maxPostBodyBytes));
        }

        MaxGetTargetBytes = maxGetTargetBytes;
        MaxPostBodyBytes = maxPostBodyBytes;
    }

    /// <summary>Gets the largest measured GET request-target, in bytes.</summary>
    public int MaxGetTargetBytes { get; }

    /// <summary>Gets the largest measured POST form body, in bytes, or <see langword="null"/> for a GET-only shard.</summary>
    public int? MaxPostBodyBytes { get; }
}

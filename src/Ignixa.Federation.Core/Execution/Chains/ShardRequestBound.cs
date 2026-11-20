using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// An operator-trusted transport ceiling for one shard: the largest initial request the shard is known to accept by the
/// method its chain witness and rewritten primary searches use, which is POST search when its profile supports it and GET
/// otherwise. <see cref="ForwardChainResolver"/> rejects a bound for any other method rather than guess its size.
/// </summary>
/// <remarks>
/// <see cref="ForwardChainResolver"/> measures each complete initial request in UTF-8 bytes before sending or exposing it:
/// the form-encoded body for <see cref="ShardSearchMethod.PostSearch"/>, or the request-target path of the shard's
/// canonical base plus <c>/Type?</c> and the encoded query for <see cref="ShardSearchMethod.Get"/>. Both include every
/// separator and the largest <c>_count</c> the consuming executor can send. Headers, the scheme and the host are not
/// counted, so the bound must leave room for them. A request over the bound is refused rather than split. The bound is a
/// configured fact, never inferred from a backend profile, and it is not enforcement on the wire.
/// </remarks>
public sealed class ShardRequestBound
{
    /// <summary>Initializes a new instance of the <see cref="ShardRequestBound"/> class.</summary>
    /// <param name="method">The method this bound measures; it must be the method the shard's profile rewrites with.</param>
    /// <param name="maxRequestBytes">The largest measured initial request, in bytes; positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="method"/> is undefined, or <paramref name="maxRequestBytes"/> is not positive.
    /// </exception>
    public ShardRequestBound(ShardSearchMethod method, int maxRequestBytes)
    {
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "Undefined search method.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRequestBytes);
        Method = method;
        MaxRequestBytes = maxRequestBytes;
    }

    /// <summary>Gets the method this bound measures, which the shard's witness and primary searches use.</summary>
    public ShardSearchMethod Method { get; }

    /// <summary>Gets the largest measured initial request, in bytes.</summary>
    public int MaxRequestBytes { get; }
}

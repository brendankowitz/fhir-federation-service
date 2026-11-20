using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// An operator-trusted transport ceiling for one shard: the method its chain witness and rewritten primary searches use,
/// and the largest initial request the shard is known to accept with that method.
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
    /// <param name="method">The method used for this shard's witness and primary searches.</param>
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

    /// <summary>Gets the method used for this shard's witness and primary searches.</summary>
    public ShardSearchMethod Method { get; }

    /// <summary>Gets the largest measured initial request, in bytes.</summary>
    public int MaxRequestBytes { get; }
}

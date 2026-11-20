namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Operator-configured outbound search ceilings for one shard (design spec §7.8). These are wire byte limits used by
/// <see cref="HttpShardClient"/>, not evidence that the backend accepts requests up to either limit.
/// </summary>
/// <remarks>
/// <para><b>GET.</b> The request target in HTTP/1.1 origin-form: the path of the configured base, the type, <c>?</c> and the
/// query exactly as sent, with escapes and repeated parameters counted as written, and the <c>_count</c> the client appends.
/// The scheme, host, port and the rest of the request line and headers are not counted.</para>
/// <para><b>POST.</b> The <c>application/x-www-form-urlencoded</c> body, which is the same query text as ASCII bytes.
/// The fixed <c>POST {base}/{type}/_search</c> target is not counted.</para>
/// <para>A request that would be exactly this long is sent; one byte more is never sent, trimmed or moved to the other method.</para>
/// </remarks>
public sealed class ShardRequestLimits
{
    /// <summary>Creates the limits for one shard.</summary>
    /// <param name="maxGetTargetBytes">The most bytes of origin-form request target a GET search or GET continuation may have.</param>
    /// <param name="maxPostBodyBytes">The most bytes of form body a POST search or POST continuation may have.</param>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public ShardRequestLimits(int maxGetTargetBytes, int maxPostBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxGetTargetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPostBodyBytes);
        MaxGetTargetBytes = maxGetTargetBytes;
        MaxPostBodyBytes = maxPostBodyBytes;
    }

    /// <summary>Gets the most bytes of origin-form request target a GET may have.</summary>
    public int MaxGetTargetBytes { get; }

    /// <summary>Gets the most bytes of form body a POST may have.</summary>
    public int MaxPostBodyBytes { get; }
}

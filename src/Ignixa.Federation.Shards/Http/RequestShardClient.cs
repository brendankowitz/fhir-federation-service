using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// A request-bound view of a <see cref="MultiShardHttpClient"/>. Its searches, continuations and qualified reads share
/// the router's transports, but charge only the meter supplied to <see cref="MultiShardHttpClient.ForRequest"/>.
/// </summary>
/// <remarks>
/// The same view can serve parallel calls for one request. It does not own the router or meter and must not outlive either.
/// </remarks>
public sealed class RequestShardClient : IShardClient
{
    private readonly MultiShardHttpClient _router;
    private readonly IShardTransferMeter _meter;

    internal RequestShardClient(MultiShardHttpClient router, IShardTransferMeter meter)
    {
        _router = router;
        _meter = meter;
    }

    /// <inheritdoc cref="MultiShardHttpClient.SearchAsync(ShardSearchRequest, CancellationToken)"/>
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken) =>
        _router.SearchMeteredAsync(request, _meter, cancellationToken);

    /// <inheritdoc cref="MultiShardHttpClient.ContinueAsync(ShardCursor, CancellationToken)"/>
    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
        _router.ContinueMeteredAsync(cursor, _meter, cancellationToken);

    /// <inheritdoc cref="MultiShardHttpClient.ReadAsync(ResourceKey, CancellationToken)"/>
    public Task<ShardReadResult> ReadAsync(ResourceKey key, CancellationToken cancellationToken) =>
        _router.ReadMeteredAsync(key, _meter, cancellationToken);
}

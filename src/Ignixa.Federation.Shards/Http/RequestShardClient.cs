using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// A request-bound view of a <see cref="MultiShardHttpClient"/>. Its searches, continuations and qualified reads share
/// the router's transports, but charge only the request's meter and carry only its explicitly supplied caller identifier, if any.
/// </summary>
/// <remarks>
/// The same view can serve parallel calls for one request. It does not own the router or meter and must not outlive either.
/// </remarks>
public sealed class RequestShardClient : IShardClient
{
    private readonly MultiShardHttpClient _router;
    private readonly ShardRequestContext _context;

    internal RequestShardClient(MultiShardHttpClient router, ShardRequestContext context)
    {
        _router = router;
        _context = context;
    }

    /// <inheritdoc cref="MultiShardHttpClient.SearchAsync(ShardSearchRequest, CancellationToken)"/>
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken) =>
        _router.SearchWithContextAsync(request, _context, cancellationToken);

    /// <inheritdoc cref="MultiShardHttpClient.ContinueAsync(ShardCursor, CancellationToken)"/>
    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
        _router.ContinueWithContextAsync(cursor, _context, cancellationToken);

    /// <inheritdoc cref="MultiShardHttpClient.ReadAsync(ResourceKey, CancellationToken)"/>
    public Task<ShardReadResult> ReadAsync(ResourceKey key, CancellationToken cancellationToken) =>
        _router.ReadWithContextAsync(key, _context, cancellationToken);
}

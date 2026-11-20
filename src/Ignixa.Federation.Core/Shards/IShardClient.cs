namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// Searches one shard, one backend page per call (design spec §7). The HTTP implementation lives in
/// <c>Ignixa.Federation.Shards</c>; the planner depends only on this contract.
/// </summary>
/// <remarks>
/// <para>Every call sends strict handling (<c>Prefer: handling=strict</c>, §6.2), so a backend rejects a parameter it cannot honour
/// rather than ignoring it.</para>
/// <para>Failures throw. An implementation never turns a backend error, timeout or unreadable response into an empty
/// page, because the planner would then return wrong results that look complete (§8).</para>
/// <para>Cancellation propagates: when the token is cancelled the call throws <see cref="OperationCanceledException"/>.</para>
/// </remarks>
public interface IShardClient
{
    /// <summary>Runs a search and returns its first backend page.</summary>
    /// <param name="request">The search to run.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The first page.</returns>
    Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken);

    /// <summary>Fetches the page a cursor points to.</summary>
    /// <param name="cursor">A cursor from a previous page's <see cref="ShardPage.Next"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The following page.</returns>
    Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken);
}

using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Fetches one registered shard's advertisement for <see cref="ShardCapabilityRefresher"/>. This is the seam to
/// <see cref="Http.HttpShardClient.GetCapabilitiesAsync"/>, not a second way to call a shard.
/// </summary>
/// <param name="shard">The registered shard to fetch.</param>
/// <param name="cancellationToken">Cancelled when the refresher is stopped.</param>
/// <returns>The evidence the shard advertises.</returns>
/// <remarks>
/// An implementation must keep the metadata fetch contract: one bounded exchange (the client's own deadline and byte
/// limit; this seam adds no timeout and no retry), a classified <see cref="Http.ShardCallException"/> for every failure
/// the backend or wire causes, and <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/>
/// is cancelled. Any other exception is a programming error: the refresher does not catch it.
/// </remarks>
public delegate Task<ShardCapabilityEvidence> ShardCapabilityFetch(ShardDescriptor shard, CancellationToken cancellationToken);

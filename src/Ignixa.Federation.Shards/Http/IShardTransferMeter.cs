using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Observes the backend sends and response bytes <see cref="HttpShardClient"/> actually transfers, so request-wide budget
/// accounting (design spec §7.8) counts measured traffic rather than estimating it from resources.
/// </summary>
/// <remarks>
/// <para>Calls happen on the caller's asynchronous flow, so an implementation can attribute them to an ambient request
/// scope. The per-call byte and time limits are enforced by the client whether or not a meter is present.</para>
/// <para>Exceptions propagate unchanged, so a meter can stop a call that would exceed a budget: throwing from
/// <see cref="RecordSend"/> prevents the send, and throwing from <see cref="RecordResponseBytes"/> stops the body read.</para>
/// </remarks>
public interface IShardTransferMeter
{
    /// <summary>Records one attempted send, immediately before the request leaves for the backend.</summary>
    /// <param name="shard">The shard the request goes to.</param>
    void RecordSend(ShardId shard);

    /// <summary>Records response body bytes as each chunk is read, before the per-call byte limit is checked.</summary>
    /// <param name="shard">The shard that sent the bytes.</param>
    /// <param name="count">The number of bytes in the chunk; always greater than zero.</param>
    void RecordResponseBytes(ShardId shard, int count);
}

using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Observes the backend sends and response bytes <see cref="HttpShardClient"/> actually transfers, so request-wide budget
/// accounting (design spec §7.8) counts measured traffic rather than estimating it from resources.
/// </summary>
/// <remarks>
/// <para>Calls happen on the caller's asynchronous flow, so an implementation can attribute them to an ambient request
/// scope. The per-call byte and time limits are enforced by the client whether or not a meter is present.</para>
/// <para>What is counted is what the client itself does: one logical send attempt per call, and the response body bytes
/// it consumes. A body over the limit is consumed to one byte past it, and that byte is counted; a rejected body is never
/// drained. Headers, TLS records, handshakes and connection-pool activity are not counted.</para>
/// <para>Exceptions propagate unchanged, so a meter can stop a call that would exceed a budget: throwing from
/// <see cref="RecordSend"/> prevents the send, and throwing from <see cref="RecordResponseBytes"/> stops the body read.
/// A meter's own exception, even an <see cref="IOException"/> or <see cref="HttpRequestException"/>, is never classified
/// as a transport failure.</para>
/// </remarks>
public interface IShardTransferMeter
{
    /// <summary>Records one logical send attempt, immediately before the request is handed to the connection.</summary>
    /// <param name="shard">The shard the request goes to.</param>
    void RecordSend(ShardId shard);

    /// <summary>Records response body bytes as each chunk is consumed, before the per-call byte limit is checked.</summary>
    /// <param name="shard">The shard that sent the bytes.</param>
    /// <param name="count">The number of bytes in the chunk; always greater than zero.</param>
    void RecordResponseBytes(ShardId shard, int count);
}

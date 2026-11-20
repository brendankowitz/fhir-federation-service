using System.Collections.Concurrent;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>Records what the client reports to its transfer meter.</summary>
internal sealed class RecordingMeter : IShardTransferMeter
{
    private readonly ConcurrentQueue<ShardId> _sends = new();
    private readonly ConcurrentQueue<(ShardId Shard, int Count)> _chunks = new();

    /// <summary>Gets or sets an exception thrown from <see cref="RecordSend"/>, as a budget-enforcing meter would.</summary>
    public Exception? ThrowOnSend { get; set; }

    /// <summary>Gets or sets what runs after each recorded chunk; it may throw or cancel, as a budget-enforcing meter would.</summary>
    public Action<int>? OnBytes { get; set; }

    public IReadOnlyList<ShardId> Sends => [.. _sends];

    public IReadOnlyList<(ShardId Shard, int Count)> Chunks => [.. _chunks];

    public long Bytes => _chunks.Sum(c => (long)c.Count);

    public void RecordSend(ShardId shard)
    {
        _sends.Enqueue(shard);
        if (ThrowOnSend is { } exception)
        {
            throw exception;
        }
    }

    public void RecordResponseBytes(ShardId shard, int count)
    {
        _chunks.Enqueue((shard, count));
        OnBytes?.Invoke(count);
    }
}

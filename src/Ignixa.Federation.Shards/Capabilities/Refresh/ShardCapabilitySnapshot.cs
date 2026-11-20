using System.Collections.Immutable;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>One coherent, immutable view of every registered shard's <see cref="ShardCapabilityEntry"/>.</summary>
/// <remarks>A published snapshot never changes; the refresher publishes a new one instead.</remarks>
public sealed class ShardCapabilitySnapshot
{
    private readonly ImmutableDictionary<ShardId, ShardCapabilityEntry> _shards;

    internal ShardCapabilitySnapshot(ImmutableDictionary<ShardId, ShardCapabilityEntry> shards)
    {
        _shards = shards;
    }

    /// <summary>Gets one entry for every shard in the registry, keyed by shard identifier.</summary>
    public IReadOnlyDictionary<ShardId, ShardCapabilityEntry> Shards => _shards;

    /// <summary>Gets whether every registered shard has supplied evidence. This does not indicate readiness or certification.</summary>
    public bool IsComplete => _shards.Values.All(entry => !entry.IsIncomplete);

    /// <summary>Gets whether any shard's latest fetch failed after it had previously supplied evidence.</summary>
    public bool IsDegraded => _shards.Values.Any(entry => entry.IsDegraded);

    internal ShardCapabilitySnapshot With(ShardId shard, ShardCapabilityEntry entry) => new(_shards.SetItem(shard, entry));
}

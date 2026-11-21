using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>One clause's shared limits and physical keys. Discovery order is registry, page, entry, then literal.</summary>
internal sealed class ReverseChainWitnesses(ReferencePath path, int maxRows, int maxKeys)
{
    private readonly Dictionary<ResourceKey, (int ShardOrdinal, long Position)> _first = [];
    private readonly Lock _lock = new();
    private long _rows;

    public void ChargeRows(int count)
    {
        if (Interlocked.Add(ref _rows, count) > maxRows)
        {
            throw new ReverseChainRefusedException(path, ReverseChainRefusal.TooCostly);
        }
    }

    public void Record(ResourceKey key, int shardOrdinal, long position)
    {
        lock (_lock)
        {
            if (_first.TryGetValue(key, out var previous))
            {
                if (previous.ShardOrdinal <= shardOrdinal)
                {
                    return;
                }
            }
            else if (_first.Count == maxKeys)
            {
                throw new ReverseChainRefusedException(path, ReverseChainRefusal.TooCostly);
            }

            _first[key] = (shardOrdinal, position);
        }
    }

    /// <summary>Reads the final keys once every writer has finished, independent of call completion order.</summary>
    public List<ResourceKey> OrderedKeys() => [.. _first.OrderBy(pair => pair.Value).Select(pair => pair.Key)];
}

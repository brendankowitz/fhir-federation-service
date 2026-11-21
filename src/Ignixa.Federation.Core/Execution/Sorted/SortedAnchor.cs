using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// The last match a sorted stream emitted: its <c>_lastUpdated</c> instant and its federation key (design spec §7.4). A
/// replay must find this exact pair on the saved input, or the continuation fails.
/// </summary>
public sealed class SortedAnchor
{
    /// <summary>Initializes a new instance of the <see cref="SortedAnchor"/> class.</summary>
    /// <param name="lastUpdated">The emitted entry's <c>meta.lastUpdated</c>, as the shard returned it.</param>
    /// <param name="key">The emitted entry's federation key.</param>
    public SortedAnchor(DateTimeOffset lastUpdated, ResourceKey key)
    {
        LastUpdated = lastUpdated;
        Key = key;
    }

    /// <summary>Gets the emitted entry's <c>meta.lastUpdated</c>, at full returned precision.</summary>
    public DateTimeOffset LastUpdated { get; }

    /// <summary>Gets the emitted entry's federation key.</summary>
    public ResourceKey Key { get; }
}

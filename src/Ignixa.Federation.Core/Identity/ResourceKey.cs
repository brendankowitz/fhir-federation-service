using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Identity;

/// <summary>
/// The identity of a resource across the federation (design spec §4.1): the shard that holds it, its type and its
/// logical id. Equality is exactly these three parts; two <c>Patient/A1</c> on different shards are two resources.
/// </summary>
/// <param name="Shard">The shard that holds the resource.</param>
/// <param name="Type">The resource type.</param>
/// <param name="Id">The logical id on that shard.</param>
public sealed record ResourceKey(ShardId Shard, ResourceType Type, ResourceId Id)
{
    /// <summary>Gets the shard that holds the resource.</summary>
    public ShardId Shard { get; } = Shard ?? throw new ArgumentNullException(nameof(Shard));

    /// <summary>Gets the resource type.</summary>
    public ResourceType Type { get; } = Type ?? throw new ArgumentNullException(nameof(Type));

    /// <summary>Gets the logical id on that shard.</summary>
    public ResourceId Id { get; } = Id ?? throw new ArgumentNullException(nameof(Id));
}

using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Shards;

/// <summary>One resource returned by a shard, with its identity and its exact bytes (design spec §7, §11).</summary>
public sealed class ShardEntry
{
    /// <summary>Initializes a new instance of the <see cref="ShardEntry"/> class.</summary>
    /// <param name="key">The resource's federation identity.</param>
    /// <param name="versionId">The backend's <c>meta.versionId</c>, when present.</param>
    /// <param name="lastUpdated">The backend's <c>meta.lastUpdated</c>, when present.</param>
    /// <param name="resource">The exact resource bytes as the backend returned them; copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty.</exception>
    public ShardEntry(ResourceKey key, string? versionId, DateTimeOffset? lastUpdated, ReadOnlySpan<byte> resource)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (resource.IsEmpty)
        {
            throw new ArgumentException("Resource bytes must not be empty.", nameof(resource));
        }

        Key = key;
        VersionId = versionId;
        LastUpdated = lastUpdated;
        Resource = resource.ToArray();
    }

    /// <summary>Gets the resource's federation identity.</summary>
    public ResourceKey Key { get; }

    /// <summary>Gets the backend's <c>meta.versionId</c>, or <see langword="null"/> when absent.</summary>
    public string? VersionId { get; }

    /// <summary>Gets the backend's <c>meta.lastUpdated</c>, or <see langword="null"/> when absent.</summary>
    public DateTimeOffset? LastUpdated { get; }

    /// <summary>
    /// Gets the exact resource bytes as the backend returned them. Owned by this entry: a copy taken on construction,
    /// so a pooled or reused source buffer cannot change them. Never empty.
    /// </summary>
    public ReadOnlyMemory<byte> Resource { get; }
}

using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// A resource in a federated search result. Its identity is <see cref="FullUrl"/> (the resource's real URL on its
/// shard), never type and id: two <c>Patient/123</c> from different shards are two different entries.
/// </summary>
public sealed class FederatedEntry : IEquatable<FederatedEntry>
{
    public required Uri FullUrl { get; init; }

    public required string ResourceType { get; init; }

    public required string Id { get; init; }

    public required EntryMode Mode { get; init; }

    /// <summary>The shard alias from the <c>source-shard</c> extension, if present.</summary>
    public string? SourceShard { get; init; }

    /// <summary>The federation-proxied read URL (<c>entry.link</c> relation <c>alternate</c>), if the federation offers one.</summary>
    public Uri? Alternate { get; init; }

    /// <summary>The resource exactly as returned; cloned, so it stays valid after the response is disposed.</summary>
    public required JsonElement Resource { get; init; }

    public bool Equals(FederatedEntry? other) => other is not null && FullUrl == other.FullUrl;

    public override bool Equals(object? obj) => Equals(obj as FederatedEntry);

    public override int GetHashCode() => FullUrl.GetHashCode();

    /// <summary>Identifies the entry without printing the resource body.</summary>
    public override string ToString() => $"{Mode} {FullUrl}";
}

namespace Ignixa.Federation.Shards.Http;

/// <summary>The current version of one resource as its shard returned it (design spec §5.1).</summary>
/// <remarks>
/// Made only by <see cref="HttpShardClient.ReadAsync"/>, which has checked that the bytes are one JSON object whose
/// <c>resourceType</c> and <c>id</c> are those of the requested key. Nothing else the backend sent, such as a
/// <c>Content-Location</c> header or a reference inside the resource, is carried or trusted.
/// </remarks>
public sealed class ShardReadResult
{
    internal ShardReadResult(Uri contentLocation, ReadOnlySpan<byte> resource)
    {
        ContentLocation = contentLocation;
        Resource = resource.ToArray();
    }

    /// <summary>
    /// Gets the absolute URL the resource was read from: the shard's registered base followed by the type and id. It is
    /// derived by the client, never taken from the response.
    /// </summary>
    public Uri ContentLocation { get; }

    /// <summary>Gets the exact response body, byte for byte. Owned by this result: a copy taken on construction.</summary>
    public ReadOnlyMemory<byte> Resource { get; }
}

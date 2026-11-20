namespace Ignixa.Federation.Shards.Http;

/// <summary>The current version of one resource as its shard returned it (design spec §5.1).</summary>
/// <remarks>
/// Made only by <see cref="HttpShardClient.ReadAsync"/>, which has checked that the bytes are one JSON object whose
/// <c>resourceType</c> and <c>id</c> are those of the requested key. Neither a backend <c>Content-Location</c> header
/// nor a reference inside the resource affects the trusted address.
/// </remarks>
public sealed class ShardReadResult
{
    internal ShardReadResult(Uri contentLocation, ReadOnlyMemory<byte> resource)
    {
        ContentLocation = contentLocation;
        Resource = resource;
    }

    /// <summary>
    /// Gets the absolute URL the resource was read from: the shard's registered base followed by the type and id. It is
    /// derived by the client, never taken from the response.
    /// </summary>
    public Uri ContentLocation { get; }

    /// <summary>Gets the exact response body, byte for byte, in a buffer owned by this read.</summary>
    public ReadOnlyMemory<byte> Resource { get; }
}

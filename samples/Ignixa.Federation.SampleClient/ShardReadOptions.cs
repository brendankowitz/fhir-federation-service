namespace Ignixa.Federation.SampleClient;

/// <summary>A shard the client may read directly, with the client's own credential for it.</summary>
public sealed class ShardReadOptions
{
    /// <summary>The shard base URL, as it appears in <c>fullUrl</c>. Same rules as the federation base URL.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// Supplies the client's own bearer token for this shard, called once per request. Independent of the federation
    /// credential; <c>null</c> (or unset) reads anonymously.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? AccessToken { get; init; }
}

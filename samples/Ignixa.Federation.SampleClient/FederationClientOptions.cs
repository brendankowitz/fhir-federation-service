namespace Ignixa.Federation.SampleClient;

/// <summary>How the client talks to the federation, and which shards it may read directly.</summary>
internal sealed class FederationClientOptions
{
    /// <summary>
    /// The federation base URL: absolute <c>https</c>, no user info, query or fragment. A trailing <c>/</c> is added
    /// if missing.
    /// </summary>
    public required Uri FederationBaseUrl { get; init; }

    /// <summary>
    /// Supplies the bearer token for federation requests, called once per request. Returning <c>null</c>
    /// (or leaving this unset) sends the request anonymously. This token is never sent to a shard.
    /// </summary>
    public Func<CancellationToken, ValueTask<string?>>? FederationAccessToken { get; init; }

    /// <summary>
    /// Shards the client may read directly via <c>entry.fullUrl</c>. Empty by default: direct reads are opt-in.
    /// </summary>
    public IReadOnlyList<ShardReadOptions> DirectReadShards { get; init; } = [];
}

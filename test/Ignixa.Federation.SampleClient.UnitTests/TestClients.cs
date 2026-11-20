namespace Ignixa.Federation.SampleClient.UnitTests;

internal static class TestClients
{
    public const string FederationToken = "fake-fed-token";
    public const string ShardToken = "fake-shard-token";
    public const string Fed = "https://fed.example/fhir/";
    public const string East = "https://shard-east.example/fhir/";
    public const string West = "https://shard-west.example/fhir/";

    public static Func<CancellationToken, ValueTask<string?>> Token(string? value) =>
        _ => ValueTask.FromResult(value);

    /// <summary>A client with a federation credential and no direct-read shards.</summary>
    public static FederationClient Create(ScriptedHandler federation, string baseUrl = Fed, string? token = FederationToken) =>
        new(
            new FederationClientOptions { FederationBaseUrl = new Uri(baseUrl), FederationAccessToken = Token(token) },
            federation);

    /// <summary>A client with direct-read shards; each shard gets its own handler, keyed by host name.</summary>
    public static FederationClient CreateWithShards(
        ScriptedHandler federation, Dictionary<string, ScriptedHandler> shardHandlers, params ShardReadOptions[] shards) =>
        new(
            new FederationClientOptions
            {
                FederationBaseUrl = new Uri(Fed),
                FederationAccessToken = Token(FederationToken),
                DirectReadShards = shards,
            },
            federation,
            shardBase => shardHandlers[shardBase.Host]);

    public static ShardReadOptions Shard(string baseUrl, string? token) =>
        new() { BaseUrl = new Uri(baseUrl), AccessToken = Token(token) };

    public static Task<SearchResultSet> SearchAll(FederationClient client, string type = "Patient") =>
        SearchResultSet.FromAsync(client.SearchAsync(type, [], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
}

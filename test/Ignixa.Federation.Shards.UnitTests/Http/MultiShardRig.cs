using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// Two synthetic shards, <c>east</c> and <c>west</c>, at two <see cref="LoopbackFhirServer"/>s in one registry, and a
/// <see cref="MultiShardHttpClient"/> over them that trusts only the generated loopback leaf. Both shards share one
/// credential provider and one transfer meter, as the production router does.
/// </summary>
internal sealed class MultiShardRig : IAsyncDisposable
{
    public static readonly ShardId EastId = ShardId.Parse("east");

    public static readonly ShardId WestId = ShardId.Parse("west");

    public static readonly ShardId GhostId = ShardId.Parse("ghost");

    private readonly TimeSpan _callTimeout;
    private readonly long _maxResponseBytes;
    private readonly Dictionary<ShardId, ShardRequestLimits> _limits;

    private MultiShardRig(TimeSpan callTimeout, long maxResponseBytes, ShardRequestLimits eastLimits, ShardRequestLimits westLimits)
    {
        _callTimeout = callTimeout;
        _maxResponseBytes = maxResponseBytes;
        _limits = new() { [EastId] = eastLimits, [WestId] = westLimits };
        East = new LoopbackFhirServer();
        West = new LoopbackFhirServer();
        Registry = CreateRegistry();
        Router = CreateRouter(Registry);
    }

    public LoopbackFhirServer East { get; }

    public LoopbackFhirServer West { get; }

    public SyntheticCredentials Credentials { get; } = new();

    public RecordingMeter Meter { get; } = new();

    public ShardRegistry Registry { get; }

    public MultiShardHttpClient Router { get; }

    public ShardDescriptor EastShard => Registry.Shards[0];

    public ShardDescriptor WestShard => Registry.Shards[1];

    /// <summary>Starts the rig; each shard's limits are <see cref="SyntheticLimits.Generous"/> unless given.</summary>
    public static MultiShardRig Start(TimeSpan? callTimeout = null, long maxResponseBytes = 1 << 20, ShardRequestLimits? eastLimits = null, ShardRequestLimits? westLimits = null) =>
        new(callTimeout ?? TimeSpan.FromSeconds(10), maxResponseBytes, eastLimits ?? SyntheticLimits.Generous, westLimits ?? SyntheticLimits.Generous);

    /// <summary>The same configuration built again: its descriptors are equal in value to this registry's but are not the same objects.</summary>
    public ShardRegistry CreateRegistry() =>
        ShardRegistry.Create(
            [Options("east", "k7q2", East), Options("west", "p5w8", West)],
            [ShardHarness.Follow]);

    /// <summary>Another router over <paramref name="registry"/> on this rig's credentials and meter, trusting only the loopback leaf.</summary>
    public MultiShardHttpClient CreateRouter(ShardRegistry registry) => CreateRouter(registry, Meter);

    /// <summary>Another router over <paramref name="registry"/> on this rig's credentials, with <paramref name="meter"/> as its default meter (or none), trusting only the loopback leaf.</summary>
    public MultiShardHttpClient CreateRouter(ShardRegistry registry, IShardTransferMeter? meter) =>
        new(registry, Credentials, _callTimeout, _maxResponseBytes, _limits, meter, LoopbackCertificate.Trust);

    /// <summary>A GET search for <c>name=Sarah</c>, page size 10, of <paramref name="shard"/>.</summary>
    public static ShardSearchRequest Search(ShardId shard) =>
        new(shard, ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 10, ShardSearchMethod.Get);

    public static ResourceKey Key(ShardId shard, string id) => new(shard, ShardHarness.Patient, ResourceId.Parse(id));

    /// <summary>A searchset that passes the self check for <see cref="Search"/> against <paramref name="server"/>.</summary>
    public static string Page(LoopbackFhirServer server, string? next = null, params string[] entries) =>
        Searchsets.Page(server.BaseUrl + "/Patient?name=Sarah&_count=10", next, total: null, entries);

    public static string PatientJson(string id, string note) =>
        $"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"note\":\"{note}\"}}";

    public async ValueTask DisposeAsync()
    {
        Router.Dispose();
        await East.DisposeAsync();
        await West.DisposeAsync();
    }

    private static ShardOptions Options(string id, string alias, LoopbackFhirServer server) =>
        new()
        {
            Id = id,
            Alias = alias,
            BaseUrl = server.BaseUrl,
            Profile = ShardHarness.Follow.Name,
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.synthetic.example" },
        };
}

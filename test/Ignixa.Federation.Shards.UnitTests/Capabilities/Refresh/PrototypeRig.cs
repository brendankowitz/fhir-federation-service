using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Ignixa.Federation.Shards.UnitTests.Probes;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// Two loopback shards, <c>east</c> and <c>west</c>, registered with the prototype catalog's profiles (or others a test
/// names), the real router over them, and a refresher whose fetch is the router's real metadata exchange, on a manual
/// clock. By default east serves the pinned Ignixa advertisement and west the pinned Microsoft one.
/// </summary>
internal sealed class PrototypeRig : IAsyncDisposable
{
    private const string Alias = "k7q2";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly BackendProfile _eastProfile;
    private readonly BackendProfile _westProfile;

    private PrototypeRig(
        BackendProfile east,
        BackendProfile west,
        string eastStatement,
        string westStatement,
        Func<MultiShardHttpClient, ShardCapabilityFetch>? fetch)
    {
        _eastProfile = east;
        _westProfile = west;
        East = new LoopbackFhirServer();
        West = new LoopbackFhirServer();
        EastRoutes = new ProbeRoutes(East) { Advertisement = ScriptedResponse.Json(eastStatement) };
        WestRoutes = new ProbeRoutes(West) { Advertisement = ScriptedResponse.Json(westStatement) };
        Registry = CreateRegistry();
        Router = Route(Registry);
        Refresher = new ShardCapabilityRefresher(
            Registry,
            fetch?.Invoke(Router) ?? ((shard, cancellationToken) => Router.GetClient(shard).GetCapabilitiesAsync(cancellationToken)),
            Time);
        _run = Refresher.RunAsync(_stop.Token);
    }


    public LoopbackFhirServer East { get; }

    public LoopbackFhirServer West { get; }

    public ProbeRoutes EastRoutes { get; }

    public ProbeRoutes WestRoutes { get; }

    public ShardRegistry Registry { get; }

    public MultiShardHttpClient Router { get; }

    public ManualTimeProvider Time { get; } = new();

    public ShardCapabilityRefresher Refresher { get; }

    public ShardDescriptor EastShard => Registry.Shards[0];

    public ShardDescriptor WestShard => Registry.Shards[1];

    public IEnumerable<string> EastWires => East.Requests.Select(ProbeScript.Wire);

    public IEnumerable<string> WestWires => West.Requests.Select(ProbeScript.Wire);

    /// <summary>Starts the rig and returns once the refresher's first sweep has published both shards.</summary>
    public static async Task<PrototypeRig> StartAsync(
        BackendProfile? east = null,
        BackendProfile? west = null,
        string? eastStatement = null,
        string? westStatement = null,
        Func<MultiShardHttpClient, ShardCapabilityFetch>? fetch = null)
    {
        var rig = new PrototypeRig(
            east ?? PrototypeBackendCatalog.Profiles[0],
            west ?? PrototypeBackendCatalog.Profiles[1],
            eastStatement ?? PrototypeFixtures.East,
            westStatement ?? PrototypeFixtures.West,
            fetch);
        await Watchdog.EventuallyAsync(
            () => rig.Refresher.Current.Shards.Values.All(entry => entry is { Evidence: not null } or { Failure: not null }),
            "the first capability sweep to be published");
        return rig;
    }

    /// <summary>Scripts both shards to pass every probe of a GET-only, next-link-following profile.</summary>
    public void SucceedEverywhere()
    {
        EastRoutes.Succeed("Patient", ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        WestRoutes.Succeed("Patient", ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
    }

    /// <summary>Runs one more refresh sweep and returns once every shard's result from it is published (the sweep publishes per shard).</summary>
    public async Task RefreshAsync()
    {
        ShardCapabilitySnapshot before = Refresher.Current;
        Time.Advance(TimeSpan.FromMinutes(10));
        await Watchdog.EventuallyAsync(
            () => Refresher.Current.Shards.All(entry => !ReferenceEquals(entry.Value, before.Shards[entry.Key])),
            "the refresh of every shard to be published");
    }

    public Task<PrototypeBackendAdmission?> AdmitAsync(CancellationToken cancellationToken) =>
        PrototypeBackendCatalog.TryAdmitAsync(Router, Refresher, cancellationToken);

    /// <summary>The same configuration built again: its descriptors are equal in value to this registry's but are not the same objects.</summary>
    public ShardRegistry CreateRegistry() =>
        ShardRegistry.Create(
            [Options("east", Alias, East, _eastProfile), Options("west", "p5w8", West, _westProfile)],
            [_eastProfile, _westProfile]);

    /// <summary>Another router over <paramref name="registry"/> that trusts only the loopback leaf.</summary>
    public static MultiShardHttpClient Route(ShardRegistry registry) =>
        new(
            registry,
            new SyntheticCredentials(),
            TimeSpan.FromSeconds(10),
            1 << 20,
            registry.Shards.ToDictionary(shard => shard.Id, _ => SyntheticLimits.Generous),
            new RecordingMeter(),
            LoopbackCertificate.Trust);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(Watchdog.Interval);
        }
        finally
        {
            _stop.Dispose();
            Router.Dispose();
            await East.DisposeAsync();
            await West.DisposeAsync();
        }
    }

    private static ShardOptions Options(string id, string alias, LoopbackFhirServer server, BackendProfile profile) =>
        new()
        {
            Id = id,
            Alias = alias,
            BaseUrl = server.BaseUrl,
            Profile = profile.Name,
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.synthetic.example" },
        };
}

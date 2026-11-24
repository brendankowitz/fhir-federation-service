using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.UnitTests.Http;
using Ignixa.Federation.Shards.UnitTests.Probes;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// One loopback shard, its real client, and a <see cref="ShardCapabilityRefresher"/> whose fetch is that client's real
/// metadata exchange (unless a test supplies another), driven by a manual clock. Admission, probes and refreshes all
/// travel over the same scripted wire, so a test observes only what a shard would see.
/// </summary>
internal sealed class AdmissionRig : IAsyncDisposable
{
    public const string ProductName = "SyntheticFhir";
    public const string Version = "1.2.3";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;

    private AdmissionRig(ShardHarness shard, ProbeRoutes routes, ShardCapabilityFetch? fetch)
    {
        Shard = shard;
        Routes = routes;
        Refresher = new ShardCapabilityRefresher(
            shard.Registry,
            fetch ?? ((_, cancellationToken) => shard.Client.GetCapabilitiesAsync(cancellationToken)),
            Time);
        _run = Refresher.RunAsync(_stop.Token);
    }

    public static ResourceType Patient => ShardHarness.Patient;

    public static SoftwareIdentityPolicy Policy => SoftwareIdentityPolicy.Exact(ProductName, Version);

    public ShardHarness Shard { get; }

    public ProbeRoutes Routes { get; }

    public ManualTimeProvider Time { get; } = new();

    public ShardCapabilityRefresher Refresher { get; }

    public SearchCertification Certification { get; } = new(
        new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);

    /// <summary>The wire exchanges the server received, metadata fetches included.</summary>
    public IEnumerable<string> Wires => Shard.Server.Requests.Select(ProbeScript.Wire);

    public ShardCapabilityEvidence? Evidence => Refresher.Current.Shards[Shard.Shard.Id].Evidence;

    /// <summary>An advertisement of <paramref name="type"/> search, reporting the given software version (none when <see langword="null"/>).</summary>
    public static ScriptedResponse Advertise(string type = "Patient", string? version = Version) => ScriptedResponse.Json(Statement(type, version));

    /// <summary>The statement <see cref="Advertise"/> answers with.</summary>
    public static string Statement(string type = "Patient", string? version = Version) =>
        CapabilityJson.ServerStatement(
            CapabilityJson.Arr(CapabilityJson.SearchableResource(type)),
            extraRoot: version is null ? "" : $$""","software":{"name":"{{ProductName}}","version":"{{version}}"}""");

    /// <summary>A fetch that answers every shard with <paramref name="evidence"/> and sends nothing.</summary>
    public static ShardCapabilityFetch Returning(ShardCapabilityEvidence evidence) =>
        (_, _) => Task.FromResult(evidence);

    /// <summary>Starts the rig and returns once the refresher's first fetch has been published, as evidence or a failure.</summary>
    public static async Task<AdmissionRig> StartAsync(
        BackendProfile? profile = null,
        ScriptedResponse? advertisement = null,
        Func<ShardHarness, ShardCapabilityFetch>? fetch = null)
    {
        ShardHarness shard = ShardHarness.Start(profile);
        var routes = new ProbeRoutes(shard.Server) { Advertisement = advertisement ?? Advertise() };
        var rig = new AdmissionRig(shard, routes, fetch?.Invoke(shard));
        ShardId id = shard.Shard.Id;
        await Watchdog.EventuallyAsync(
            () => rig.Refresher.Current.Shards[id] is { Evidence: not null } or { Failure: not null },
            "the first capability fetch to be published");
        return rig;
    }

    /// <summary>Runs one more refresh sweep and returns once its result is published.</summary>
    public async Task RefreshAsync()
    {
        ShardCapabilitySnapshot before = Refresher.Current;
        Time.Advance(TimeSpan.FromMinutes(10));
        await Watchdog.EventuallyAsync(() => !ReferenceEquals(Refresher.Current, before), "the refresh to be published");
    }

    public Task<ShardCapabilityProof?> ProveAsync(CancellationToken cancellationToken, ResourceType? type = null) =>
        ShardCapabilityAdmission.TryProveAsync(
            Shard.Client, Shard.Shard, Refresher, type ?? Patient, Policy, Certification, cancellationToken);

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
            await Shard.DisposeAsync();
        }
    }
}

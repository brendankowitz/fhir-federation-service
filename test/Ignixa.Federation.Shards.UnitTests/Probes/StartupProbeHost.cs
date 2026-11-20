using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// Two synthetic shards (<c>east</c>, <c>west</c>), each a real TLS loopback server at a dynamic port, composed through the
/// product registrations (<c>AddShardBackend</c>, then <c>AddShardStartupProbes</c>) into a Generic Host with defaults
/// disabled. Backend traffic stays on loopback; the host's own capability refresher runs beside the probes, as in production.
/// </summary>
internal sealed class StartupProbeHost : IAsyncDisposable
{
    public const string CheckName = "shard-startup-probes";

    public static readonly ResourceType Patient = ResourceType.Parse("Patient");

    private IHost? _host;

    public StartupProbeHost(BackendProfile eastProfile, BackendProfile westProfile)
    {
        East = new LoopbackFhirServer();
        West = new LoopbackFhirServer();
        EastRoutes = new ProbeRoutes(East);
        WestRoutes = new ProbeRoutes(West);
        Registry = ShardRegistry.Create(
            [Options("east", East, eastProfile), Options("west", West, westProfile)],
            [ShardHarness.Repost, ShardHarness.Follow, ShardHarness.GetOnly]);
    }

    public LoopbackFhirServer East { get; }

    public LoopbackFhirServer West { get; }

    public ProbeRoutes EastRoutes { get; }

    public ProbeRoutes WestRoutes { get; }

    public ShardRegistry Registry { get; }

    public ShardId EastId => Registry.Shards[0].Id;

    public ShardId WestId => Registry.Shards[1].Id;

    public IHost Host => _host ?? throw new InvalidOperationException("Build the host first.");

    public int Requests => East.Requests.Count + West.Requests.Count;

    /// <summary>Gets the one-shot probe worker the product registration added to the host.</summary>
    public ShardStartupProbeWorker Worker => Host.Services.GetServices<IHostedService>().OfType<ShardStartupProbeWorker>().Single();

    public Dictionary<ShardId, ResourceType> PatientForEveryShard() =>
        new() { [EastId] = Patient, [WestId] = Patient };

    /// <summary>Builds, without starting, a host with the shared backend and then the registrations in <paramref name="addProbes"/>.</summary>
    public IHost BuildHost(
        Action<IServiceCollection>? addProbes = null,
        IShardCredentialProvider? credentials = null,
        Action<HostOptions>? hostOptions = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton<TimeProvider>(new ManualTimeProvider());
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
            hostOptions?.Invoke(options);
        });
        var limits = Registry.Shards.ToDictionary(s => s.Id, _ => SyntheticLimits.Generous);
        builder.Services.AddShardBackend(
            Registry, credentials ?? new SyntheticCredentials(), TimeSpan.FromSeconds(10), 1 << 20, limits, LoopbackCertificate.Trust);
        (addProbes ?? (services => services.AddShardStartupProbes(PatientForEveryShard())))(builder.Services);
        _host = builder.Build();
        return _host;
    }

    /// <summary>Runs only the startup-probe health check, as the ready endpoint would for it.</summary>
    public async Task<HealthReportEntry> CheckAsync(CancellationToken cancellationToken)
    {
        HealthReport report = await Host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == CheckName, cancellationToken);
        return report.Entries[CheckName];
    }

    /// <summary>Waits, bounded, for the one-shot worker to finish its pass.</summary>
    public Task AwaitPassAsync() => Worker.ExecuteTask!.WaitAsync(Watchdog.Interval);

    /// <summary>Waits until the host's own capability refresher has fetched every shard, so its traffic is over.</summary>
    public Task AwaitRefreshAsync() =>
        Watchdog.EventuallyAsync(
            () => Host.Services.GetRequiredService<ShardCapabilityRefresher>().Current.Shards.Values.All(s => s.Evidence is not null),
            "the capability refresher to fetch every shard");

    /// <summary>The traffic the probes sent: every request except the capability statement fetches.</summary>
    public static string[] ProbeWires(LoopbackFhirServer server) =>
        [.. server.Requests.Select(ProbeScript.Wire).Where(wire => wire != ProbeRoutes.Metadata)];

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync().WaitAsync(Watchdog.Interval);
            }
        }
        finally
        {
            _host?.Dispose();
            await using var west = West;
            await East.DisposeAsync();
        }
    }

    private static ShardOptions Options(string name, LoopbackFhirServer server, BackendProfile profile) => new()
    {
        Id = $"internal-id-{name}",
        Alias = name,
        BaseUrl = server.BaseUrl,
        Profile = profile.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
    };
}

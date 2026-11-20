using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// Two synthetic shards (<c>east</c>, <c>west</c>), each a real TLS loopback server at a dynamic port with a real
/// <see cref="HttpShardClient"/>, composed into a Generic Host with defaults disabled and a manual clock. Backend
/// traffic stays on loopback.
/// </summary>
internal sealed class RefreshHostFixture : IAsyncDisposable
{
    public const string Statement =
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"instance\"," +
        "\"software\":{\"name\":\"Synthetic FHIR\",\"version\":\"9.1\"}," +
        "\"rest\":[{\"mode\":\"server\",\"resource\":[{\"type\":\"Patient\",\"interaction\":[{\"code\":\"search-type\"}]," +
        "\"searchParam\":[{\"name\":\"name\",\"type\":\"string\"}]}]}]}";

    public const string ReadyTag = "ready";

    private static readonly BackendProfile Profile = new("Synthetic", SupportsPostSearch: false, MaxCount: 50, NextLinkRedemption.FollowGet, null);

    private IHost? _host;

    public RefreshHostFixture(TimeSpan? callTimeout = null)
    {
        East = new LoopbackFhirServer();
        West = new LoopbackFhirServer();
        Registry = ShardRegistry.Create([Options("east", East), Options("west", West)], [Profile]);
        TimeSpan timeout = callTimeout ?? TimeSpan.FromSeconds(30);
        EastClient = new HttpShardClient(Registry.Shards[0], new SyntheticCredentials(), timeout, 1 << 20, null, LoopbackCertificate.Trust);
        WestClient = new HttpShardClient(Registry.Shards[1], new SyntheticCredentials(), timeout, 1 << 20, null, LoopbackCertificate.Trust);
    }

    public LoopbackFhirServer East { get; }

    public LoopbackFhirServer West { get; }

    public ShardRegistry Registry { get; }

    public HttpShardClient EastClient { get; }

    public HttpShardClient WestClient { get; }

    public ManualTimeProvider Time { get; } = new();

    public IHost Host => _host ?? throw new InvalidOperationException("Build the host first.");

    public ShardCapabilityRefresher Refresher => Host.Services.GetRequiredService<ShardCapabilityRefresher>();

    /// <summary>The correct composition: each registered descriptor gets the client built for exactly that descriptor.</summary>
    public HttpShardClient ClientFor(ShardDescriptor shard) =>
        ReferenceEquals(shard, Registry.Shards[0]) ? EastClient : WestClient;

    /// <summary>
    /// Builds, without starting, a Generic Host that registers the shard registry and the manual clock, applies
    /// <paramref name="configure"/>, then registers the capability refresh.
    /// </summary>
    public IHost BuildHost(
        Func<IServiceProvider, ShardDescriptor, HttpShardClient>? clientForShard = null,
        Action<IServiceCollection>? configure = null,
        Action<HostOptions>? hostOptions = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(Registry);
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(10);
            hostOptions?.Invoke(options);
        });
        configure?.Invoke(builder.Services);
        builder.Services.AddShardCapabilityRefresh(clientForShard ?? ((_, shard) => ClientFor(shard)));
        _host = builder.Build();
        return _host;
    }

    /// <summary>Runs the registered health checks that carry <paramref name="tag"/>, as an application's ready endpoint would.</summary>
    public Task<HealthReport> CheckAsync(string tag, CancellationToken cancellationToken) =>
        Host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains(tag), cancellationToken);

    public int Requests => East.Requests.Count + West.Requests.Count;

    /// <summary>Waits for one sweep to be published: every shard's entry is a new object, which a failed fetch also produces.</summary>
    public async Task AwaitSweepAsync(ShardCapabilitySnapshot before)
    {
        await Watchdog.EventuallyAsync(
            () => Registry.Shards.All(s => !ReferenceEquals(Refresher.Current.Shards[s.Id], before.Shards[s.Id])),
            "every shard's answer to be published");
    }

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
            EastClient.Dispose();
            WestClient.Dispose();
            await using var west = West;
            await East.DisposeAsync();
        }
    }

    private static ShardOptions Options(string name, LoopbackFhirServer server) => new()
    {
        Id = name,
        Alias = name,
        BaseUrl = server.BaseUrl,
        Profile = Profile.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
    };
}

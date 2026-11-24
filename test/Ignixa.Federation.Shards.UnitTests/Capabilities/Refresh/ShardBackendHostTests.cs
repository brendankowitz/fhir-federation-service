using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

public sealed class ShardBackendHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HostedRefreshAndMeteredTrafficUseTheSamePoolsUntilTheHostDisposesThem()
    {
        await using var east = new LoopbackFhirServer();
        await using var west = new LoopbackFhirServer();
        ShardRegistry registry = Registry(east, west);
        ShardId eastId = registry.Shards[0].Id;
        ShardId westId = registry.Shards[1].Id;
        var limits = new Dictionary<ShardId, ShardRequestLimits>
        {
            [eastId] = SyntheticLimits.Generous,
            [westId] = new(1, 1),
        };
        var credentials = new SyntheticCredentials();
        var time = new ManualTimeProvider();
        var builder = NewHost();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddShardBackend(registry, credentials, TimeSpan.FromSeconds(10), 1 << 20, limits, LoopbackCertificate.Trust);
        limits.Clear();

        string eastPage = MultiShardRig.Page(east);
        string westPatient = MultiShardRig.PatientJson("p1", "west");
        east.Respond(Advertisement(), ScriptedResponse.Json(eastPage).KeepingAlive(), Advertisement());
        west.Respond(Advertisement(), ScriptedResponse.Json(westPatient).KeepingAlive(), Advertisement());

        MultiShardHttpClient router;
        HttpShardClient borrowedEast;
        RequestShardClient requestA;
        var meterA = new RecordingMeter();
        var meterB = new RecordingMeter();
        using (IHost host = builder.Build())
        {
            await host.StartAsync(Ct);
            try
            {
                host.Services.GetRequiredService<ShardRegistry>().ShouldBeSameAs(registry);
                var refresher = host.Services.GetRequiredService<ShardCapabilityRefresher>();
                await Watchdog.EventuallyAsync(() => refresher.Current.Shards.Values.All(s => s.Evidence is not null), "startup metadata");
                ShardCapabilitySnapshot first = refresher.Current;

                router = host.Services.GetRequiredService<MultiShardHttpClient>();
                borrowedEast = router.GetClient(registry.Shards[0]);
                requestA = router.ForRequest(meterA);
                using (IServiceScope requestScope = host.Services.CreateScope())
                {
                    MultiShardHttpClient scopedRouter = requestScope.ServiceProvider.GetRequiredService<MultiShardHttpClient>();
                    scopedRouter.ShouldBeSameAs(router);
                    RequestShardClient requestB = scopedRouter.ForRequest(meterB);

                    ShardCallException refusal = await Should.ThrowAsync<ShardCallException>(() =>
                        requestB.SearchAsync(MultiShardRig.Search(westId), Ct));
                    refusal.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
                    credentials.Calls.ShouldBe(2);
                    meterB.Sends.ShouldBeEmpty();

                    await Task.WhenAll(
                        requestA.SearchAsync(MultiShardRig.Search(eastId), Ct),
                        requestB.ReadAsync(MultiShardRig.Key(westId, "p1"), Ct)).WaitAsync(Watchdog.Interval, Ct);
                }

                time.Advance(TimeSpan.FromMinutes(10));
                await Watchdog.EventuallyAsync(
                    () => registry.Shards.All(s => !ReferenceEquals(refresher.Current.Shards[s.Id], first.Shards[s.Id])),
                    "background metadata");
                refresher.Current.Shards.Values.ShouldAllBe(s => s.Evidence != null && !s.IsDegraded);
                meterA.Sends.ShouldBe([eastId]);
                meterA.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(eastPage));
                meterB.Sends.ShouldBe([westId]);
                meterB.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(westPatient));
                credentials.Calls.ShouldBe(6);
                AssertSharedConnection(east, "/fhir/Patient?name=Sarah&_count=10");
                AssertSharedConnection(west, "/fhir/Patient/p1");
            }
            finally
            {
                await host.StopAsync(Ct).WaitAsync(Watchdog.Interval, Ct);
            }
        }

        Should.Throw<ObjectDisposedException>(() => router.GetClient(registry.Shards[0]));
        await Should.ThrowAsync<ObjectDisposedException>(() => requestA.SearchAsync(MultiShardRig.Search(eastId), Ct));
        credentials.Calls.ShouldBe(6);
        await Should.ThrowAsync<ObjectDisposedException>(() => borrowedEast.GetCapabilitiesAsync(Ct));
        east.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task MissingShardLimitsFailHostStartupBeforeMetadataCanSend()
    {
        await using var east = new LoopbackFhirServer();
        await using var west = new LoopbackFhirServer();
        ShardRegistry registry = Registry(east, west);
        var credentials = new SyntheticCredentials();
        var builder = NewHost();
        builder.Services.AddShardBackend(registry, credentials, TimeSpan.FromSeconds(10), 1024,
            new Dictionary<ShardId, ShardRequestLimits> { [registry.Shards[0].Id] = SyntheticLimits.Generous });
        using IHost host = builder.Build();

        await Should.ThrowAsync<ArgumentException>(() => host.StartAsync(Ct));

        credentials.Calls.ShouldBe(0);
        east.Requests.ShouldBeEmpty();
        west.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExistingRefreshCannotSilentlyOverrideTheProductionMetadataPath()
    {
        await using var east = new LoopbackFhirServer();
        var services = new ServiceCollection();
        services.AddShardCapabilityRefresh((_, _) => throw new InvalidOperationException("Not the shared router."));
        ShardRegistry registry = ShardRegistry.Create([Options("east", east)], [ShardHarness.Follow]);
        var limits = new Dictionary<ShardId, ShardRequestLimits> { [registry.Shards[0].Id] = SyntheticLimits.Generous };

        Should.Throw<InvalidOperationException>(() =>
            services.AddShardBackend(registry, new SyntheticCredentials(), TimeSpan.FromSeconds(10), 1024, limits));
    }

    private static HostApplicationBuilder NewHost() => new(new HostApplicationBuilderSettings { DisableDefaults = true });

    private static ScriptedResponse Advertisement() => ScriptedResponse.Json(RefreshHostFixture.Statement).KeepingAlive();

    private static ShardRegistry Registry(LoopbackFhirServer east, LoopbackFhirServer west) =>
        ShardRegistry.Create([Options("east", east), Options("west", west)], [ShardHarness.Follow]);

    private static ShardOptions Options(string id, LoopbackFhirServer server) => new()
    {
        Id = id,
        Alias = id,
        BaseUrl = server.BaseUrl,
        Profile = ShardHarness.Follow.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
    };

    private static void AssertSharedConnection(LoopbackFhirServer server, string trafficTarget)
    {
        server.Requests.Select(r => r.Target).ShouldBe(["/fhir/metadata", trafficTarget, "/fhir/metadata"]);
        server.Requests.Select(r => r.Connection).Distinct().ShouldHaveSingleItem();
        foreach (RecordedRequest request in server.Requests)
        {
            request.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
        }
    }
}

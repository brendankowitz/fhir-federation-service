using System.Collections.Concurrent;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// A temporary operational credential failure at hosted capability refresh (design spec §9.1), through
/// <c>AddShardBackend</c>: the real router, real TLS loopback transport and a real Generic Host, with a manual clock and
/// two shards.
/// </summary>
public sealed class ShardBackendCredentialFailureTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedResponse Advertisement() => ScriptedResponse.Json(RefreshHostFixture.Statement).KeepingAlive();

    [Fact]
    public async Task ACredentialOutageForOneShardKeepsItsEvidenceDegradesHealthAndNeverStopsTheRefresh()
    {
        await using var east = new LoopbackFhirServer();
        await using var west = new LoopbackFhirServer();
        ShardRegistry registry = Registry(east, west);
        ShardId eastId = registry.Shards[0].Id;
        ShardId westId = registry.Shards[1].Id;
        var credentials = new OwnerCredentials();
        var time = new ManualTimeProvider();
        east.Respond(Advertisement(), Advertisement());
        west.Respond(Advertisement(), Advertisement(), Advertisement());
        using IHost host = Host(registry, credentials, time);
        var refresher = host.Services.GetRequiredService<ShardCapabilityRefresher>();

        await host.StartAsync(Ct);
        try
        {
            BackgroundService worker = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
            await Watchdog.EventuallyAsync(() => refresher.Current.IsComplete || worker.ExecuteTask!.IsCompleted, "the baseline sweep");
            worker.ExecuteTask!.IsCompleted.ShouldBeFalse();
            await AssertReadyAsync(host, HealthStatus.Healthy);
            ShardCapabilitySnapshot baseline = refresher.Current;
            ShardCapabilityEvidence eastEvidence = baseline.Shards[eastId].Evidence.ShouldNotBeNull();
            ShardCapabilityEvidence westEvidence = baseline.Shards[westId].Evidence.ShouldNotBeNull();
            east.Requests.Count.ShouldBe(1);
            west.Requests.Count.ShouldBe(1);

            // The attempted fault: east's credential is temporarily unavailable, west's is fine.
            credentials.Unavailable(OwnerCredentials.East, ShardCredentialFailure.TokenUnavailable);
            time.Advance(Interval);
            await Watchdog.EventuallyAsync(
                () => !ReferenceEquals(refresher.Current.Shards[westId], baseline.Shards[westId]) || worker.ExecuteTask!.IsCompleted,
                "the faulted sweep to reach the sibling shard");

            worker.ExecuteTask!.IsCompleted.ShouldBeFalse("a credential outage must not stop the refresh worker");
            credentials.Calls(OwnerCredentials.East).ShouldBe(2);
            east.Requests.Count.ShouldBe(1, "nothing is sent to a shard whose credential is unavailable");
            west.Requests.Count.ShouldBe(2, "the sibling is refreshed in the same sweep");

            ShardCapabilityEntry eastEntry = refresher.Current.Shards[eastId];
            eastEntry.Evidence.ShouldBeSameAs(eastEvidence);
            eastEntry.Failure.ShouldBe(ShardCallFailure.CredentialUnavailable);
            eastEntry.IsDegraded.ShouldBeTrue();
            ShardCapabilityEntry westEntry = refresher.Current.Shards[westId];
            westEntry.Failure.ShouldBeNull();
            westEntry.Evidence.ShouldNotBeNull().ShouldNotBeSameAs(westEvidence);
            await AssertReadyAsync(host, HealthStatus.Degraded);

            // Recovery: the next sweep fetches east again and clears the failure.
            ShardCapabilitySnapshot degraded = refresher.Current;
            credentials.Recover(OwnerCredentials.East);
            time.Advance(Interval);
            await Watchdog.EventuallyAsync(
                () => !ReferenceEquals(refresher.Current.Shards[westId], degraded.Shards[westId]) || worker.ExecuteTask!.IsCompleted,
                "the recovery sweep");

            worker.ExecuteTask!.IsCompleted.ShouldBeFalse();
            east.Requests.Count.ShouldBe(2);
            refresher.Current.Shards[eastId].Failure.ShouldBeNull();
            refresher.Current.Shards[eastId].Evidence.ShouldNotBeNull().ShouldNotBeSameAs(eastEvidence);
            await AssertReadyAsync(host, HealthStatus.Healthy);
        }
        finally
        {
            await host.StopAsync(Ct).WaitAsync(Watchdog.Interval, Ct);
        }
    }

    [Fact]
    public async Task StoppingTheHostWhileACredentialIsBeingAcquiredIsCancellationNotAShardFailure()
    {
        await using var east = new LoopbackFhirServer();
        await using var west = new LoopbackFhirServer();
        ShardRegistry registry = Registry(east, west);
        var credentials = new OwnerCredentials();
        credentials.Hang(OwnerCredentials.East);
        using IHost host = Host(registry, credentials, new ManualTimeProvider());
        var refresher = host.Services.GetRequiredService<ShardCapabilityRefresher>();
        ShardCapabilitySnapshot start = refresher.Current;

        await host.StartAsync(Ct);
        BackgroundService worker = host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
        await credentials.Hung.Task.WaitAsync(Watchdog.Interval, Ct);

        await host.StopAsync(Ct).WaitAsync(Watchdog.Interval, Ct);

        worker.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue("shutdown is not a failure of the worker");
        refresher.Current.ShouldBeSameAs(start, "a cancelled acquisition records nothing, not even a failure");
        east.Requests.ShouldBeEmpty();
        west.Requests.ShouldBeEmpty("shards are fetched one at a time");
    }

    private static async Task AssertReadyAsync(IHost host, HealthStatus expected)
    {
        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Tags.Contains(RefreshHostFixture.ReadyTag), Ct);

        report.Status.ShouldBe(expected);
        HealthReportEntry entry = report.Entries.ShouldHaveSingleItem().Value;
        entry.Exception.ShouldBeNull();
        entry.Data.ShouldBeEmpty();
        string text = entry.Description ?? string.Empty;
        text.ShouldNotBeEmpty();
        text.ShouldNotContain("api://", Case.Insensitive);
        text.ShouldNotContain("TokenUnavailable");
        text.ShouldNotContain("127.0.0.1");
    }

    private static IHost Host(ShardRegistry registry, IShardCredentialProvider credentials, TimeProvider time)
    {
        var limits = registry.Shards.ToDictionary(s => s.Id, _ => SyntheticLimits.Generous);
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(time);
        // A faulted worker is asserted explicitly, not left to stop the test host.
        builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        builder.Services.AddShardBackend(registry, credentials, TimeSpan.FromSeconds(10), 1 << 20, limits, LoopbackCertificate.Trust);
        return builder.Build();
    }

    private static ShardRegistry Registry(LoopbackFhirServer east, LoopbackFhirServer west) =>
        ShardRegistry.Create([Options("east", OwnerCredentials.East, east), Options("west", OwnerCredentials.West, west)], [ShardHarness.Follow]);

    private static ShardOptions Options(string id, string resource, LoopbackFhirServer server) => new()
    {
        Id = id,
        Alias = id,
        BaseUrl = server.BaseUrl,
        Profile = ShardHarness.Follow.Name,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = resource },
    };

    /// <summary>A dedicated credential double: a token for each owner (the audience in its auth), unavailable on demand for one.</summary>
    private sealed class OwnerCredentials : IShardCredentialProvider
    {
        public const string East = "api://east";
        public const string West = "api://west";

        private readonly ConcurrentDictionary<string, ShardCredentialFailure> _unavailable = new();
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private volatile string? _hungOwner;

        /// <summary>Completes when an acquisition for the hung owner has started.</summary>
        public TaskCompletionSource Hung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Unavailable(string owner, ShardCredentialFailure failure) => _unavailable[owner] = failure;

        public void Recover(string owner) => _unavailable.TryRemove(owner, out _);

        /// <summary>Makes this owner's acquisition wait until its token is cancelled, as a slow identity platform would.</summary>
        public void Hang(string owner) => _hungOwner = owner;

        public int Calls(string owner) => _calls.GetValueOrDefault(owner);

        public ValueTask<string> GetAccessTokenAsync(ShardAuth auth, CancellationToken cancellationToken)
        {
            string owner = ((ManagedIdentityAuth)auth).Resource;
            _calls.AddOrUpdate(owner, 1, (_, n) => n + 1);
            if (owner == _hungOwner)
            {
                return HangAsync(cancellationToken);
            }

            return _unavailable.TryGetValue(owner, out ShardCredentialFailure failure)
                ? throw new ShardCredentialException(failure)
                : ValueTask.FromResult(SyntheticCredentials.Token);
        }

        private async ValueTask<string> HangAsync(CancellationToken cancellationToken)
        {
            Hung.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return SyntheticCredentials.Token;
        }
    }
}

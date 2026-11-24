using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// <see cref="ShardCapabilityRefreshExtensions.AddShardCapabilityRefresh"/> in a real Generic Host: the refresher is
/// started and stopped by the host, fetches through real <see cref="HttpShardClient"/> metadata exchanges, and a
/// <c>ready</c>-tagged health check reports only what the published snapshot says. Everything is asserted from outside:
/// host services, the published snapshot, the health report, and what the loopback servers received.
/// </summary>
public sealed class ShardCapabilityRefreshHostTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedResponse Advertisement() => ScriptedResponse.Json(RefreshHostFixture.Statement);

    private static ScriptedResponse Failing() => ScriptedResponse.Status(500, "internal-secret-detail");

    private static async Task AssertReadyStatusAsync(RefreshHostFixture fixture, HealthStatus expected)
    {
        HealthReport report = await fixture.CheckAsync(RefreshHostFixture.ReadyTag, Ct);

        report.Status.ShouldBe(expected);
        HealthReportEntry entry = report.Entries.ShouldHaveSingleItem().Value;
        entry.Status.ShouldBe(expected);
        entry.Tags.ShouldContain(RefreshHostFixture.ReadyTag);
        entry.Exception.ShouldBeNull();
        entry.Data.ShouldBeEmpty();

        // Constant, safe text: nothing the backends said, no address, and no claim beyond the advertisement.
        string text = entry.Description ?? string.Empty;
        text.ShouldNotBeEmpty();
        text.ShouldNotContain("internal-secret-detail");
        text.ShouldNotContain("127.0.0.1");
        text.ShouldNotContain("synthetic-token");
        text.ShouldNotContain("verified", Case.Insensitive);
        text.ShouldNotContain("credential", Case.Insensitive);
        text.ShouldNotContain("profile", Case.Insensitive);
    }

    [Fact]
    public void RegistrationCannotSilentlyUseAStandaloneRefresherInsteadOfTheHostMetadataClients()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ShardCapabilityRefresher>(_ => throw new InvalidOperationException("Not a host-wired refresher."));

        Should.Throw<InvalidOperationException>(() => services.AddShardCapabilityRefresh((_, _) =>
            throw new InvalidOperationException("Registration must not resolve a client.")));
    }

    [Fact]
    public async Task BeforeAnyShardHasSucceededTheServiceIsUnhealthyAndTheReadSendsNothing()
    {
        await using var fixture = new RefreshHostFixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.East.Respond(ScriptedResponse.Json(RefreshHostFixture.Statement, beforeHead: token => release.Task.WaitAsync(token)));
        fixture.BuildHost();

        await AssertReadyStatusAsync(fixture, HealthStatus.Unhealthy);
        fixture.Requests.ShouldBe(0);

        await fixture.Host.StartAsync(Ct);
        await Watchdog.EventuallyAsync(() => fixture.East.Requests.Count == 1, "the startup fetch to reach the first shard");
        int sentBeforeReads = fixture.Requests;
        ShardCapabilitySnapshot before = fixture.Refresher.Current;

        await AssertReadyStatusAsync(fixture, HealthStatus.Unhealthy);
        await AssertReadyStatusAsync(fixture, HealthStatus.Unhealthy);

        fixture.Requests.ShouldBe(sentBeforeReads, "a health read must not call a backend");
        fixture.West.Requests.ShouldBeEmpty("shards are fetched one at a time");
        fixture.Refresher.Current.ShouldBeSameAs(before);
        fixture.Refresher.Current.Shards.Values.ShouldAllBe(entry => entry.IsIncomplete);
        release.SetResult();
    }

    [Fact]
    public async Task OneShardSucceedingAndAnotherNeverFetchedIsStillUnhealthyUntilTheNextSweepReachesBoth()
    {
        await using var fixture = new RefreshHostFixture();
        fixture.East.Respond(Advertisement(), Advertisement());
        fixture.West.Respond(Failing(), Advertisement());
        fixture.BuildHost();
        ShardCapabilitySnapshot start = fixture.Refresher.Current;

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitSweepAsync(start);

        fixture.Refresher.Current.Shards[fixture.Registry.Shards[0].Id].Evidence.ShouldNotBeNull();
        var west = fixture.Refresher.Current.Shards[fixture.Registry.Shards[1].Id];
        west.IsIncomplete.ShouldBeTrue();
        west.Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
        await AssertReadyStatusAsync(fixture, HealthStatus.Unhealthy);

        ShardCapabilitySnapshot afterFirst = fixture.Refresher.Current;
        fixture.Time.Advance(Interval);
        await fixture.AwaitSweepAsync(afterFirst);

        fixture.Refresher.Current.IsComplete.ShouldBeTrue();
        await AssertReadyStatusAsync(fixture, HealthStatus.Healthy);
    }

    [Fact]
    public async Task LosingAShardAfterItSucceededIsDegradedKeepingItsEvidenceAndSucceedingAgainRestoresIt()
    {
        await using var fixture = new RefreshHostFixture();
        fixture.East.Respond(Advertisement(), Advertisement(), Advertisement());
        fixture.West.Respond(Advertisement(), Failing(), Advertisement());
        fixture.BuildHost();
        var westId = fixture.Registry.Shards[1].Id;
        ShardCapabilitySnapshot snapshot = fixture.Refresher.Current;

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitSweepAsync(snapshot);
        await AssertReadyStatusAsync(fixture, HealthStatus.Healthy);
        ShardCapabilityEvidence lastKnownGood = fixture.Refresher.Current.Shards[westId].Evidence.ShouldNotBeNull();

        snapshot = fixture.Refresher.Current;
        fixture.Time.Advance(Interval);
        await fixture.AwaitSweepAsync(snapshot);

        fixture.Refresher.Current.IsComplete.ShouldBeTrue();
        fixture.Refresher.Current.IsDegraded.ShouldBeTrue();
        fixture.Refresher.Current.Shards[westId].Evidence.ShouldBeSameAs(lastKnownGood);
        fixture.Refresher.Current.Shards[westId].Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
        await AssertReadyStatusAsync(fixture, HealthStatus.Degraded);

        snapshot = fixture.Refresher.Current;
        fixture.Time.Advance(Interval);
        await fixture.AwaitSweepAsync(snapshot);

        fixture.Refresher.Current.Shards[westId].Evidence.ShouldNotBeSameAs(lastKnownGood);
        fixture.Refresher.Current.Shards[westId].Failure.ShouldBeNull();
        await AssertReadyStatusAsync(fixture, HealthStatus.Healthy);
    }

    [Fact]
    public async Task OnlyTheReadyFilterRunsTheCheckAndNeitherReadSendsAnything()
    {
        await using var fixture = new RefreshHostFixture();
        fixture.East.Respond(Advertisement());
        fixture.West.Respond(Advertisement());
        fixture.BuildHost();
        ShardCapabilitySnapshot start = fixture.Refresher.Current;
        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitSweepAsync(start);
        int sent = fixture.Requests;

        HealthReport live = await fixture.CheckAsync("live", Ct);
        await AssertReadyStatusAsync(fixture, HealthStatus.Healthy);

        live.Entries.ShouldBeEmpty();
        fixture.Requests.ShouldBe(sent);
    }

    [Fact]
    public async Task StoppingTheHostCancelsAFetchInFlightAndJoinsTheLoop()
    {
        await using var fixture = new RefreshHostFixture(callTimeout: TimeSpan.FromSeconds(60));
        fixture.East.Respond(ScriptedResponse.Stall());
        fixture.BuildHost();
        ShardCapabilitySnapshot start = fixture.Refresher.Current;
        await fixture.Host.StartAsync(Ct);
        await Watchdog.EventuallyAsync(() => fixture.East.Requests.Count == 1, "the startup fetch to stall at the first shard");

        await fixture.Host.StopAsync(Ct).WaitAsync(Watchdog.Interval, Ct);

        // A second loop can start only if the first has ended: the host joined the refresher, not merely signalled it.
        await fixture.Refresher.RunAsync(new CancellationToken(canceled: true)).WaitAsync(Watchdog.Interval, Ct);
        fixture.Refresher.Current.ShouldBeSameAs(start, "a cancelled fetch publishes nothing, not even a failure");
        fixture.Time.Advance(Interval * 2);
        fixture.Requests.ShouldBe(1, "a stopped loop sweeps no more");
    }

    [Fact]
    public async Task RegisteringTwiceKeepsTheFirstRegistrationAndOneRefresherWorkerAndCheck()
    {
        await using var fixture = new RefreshHostFixture();
        fixture.East.Respond(Advertisement());
        fixture.West.Respond(Advertisement());
        fixture.BuildHost(
            clientForShard: (_, _) => throw new InvalidOperationException("The second registration must not be used."),
            configure: services => services.AddShardCapabilityRefresh((_, shard) => fixture.ClientFor(shard)));
        ShardCapabilitySnapshot start = fixture.Refresher.Current;

        fixture.Host.Services.GetServices<IHostedService>().OfType<BackgroundService>().ShouldHaveSingleItem();
        fixture.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.ShouldHaveSingleItem();
        fixture.Host.Services.GetServices<ShardCapabilityRefresher>().ShouldHaveSingleItem().ShouldBeSameAs(fixture.Refresher);

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitSweepAsync(start);

        foreach (LoopbackFhirServer server in new[] { fixture.East, fixture.West })
        {
            RecordedRequest request = server.Requests.ShouldHaveSingleItem();
            request.Method.ShouldBe("GET");
            request.Target.ShouldBe("/fhir/metadata");
        }

        await AssertReadyStatusAsync(fixture, HealthStatus.Healthy);
    }

    [Fact]
    public async Task AClientForAnotherDescriptorIsRefusedBeforeAnythingIsSentOrPublished()
    {
        await using var fixture = new RefreshHostFixture();
        fixture.BuildHost(
            clientForShard: (_, _) => fixture.WestClient,
            hostOptions: options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        ShardCapabilitySnapshot start = fixture.Refresher.Current;

        await fixture.Host.StartAsync(Ct);
        BackgroundService worker = fixture.Host.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
        Task faulted = worker.ExecuteTask.ShouldNotBeNull();
        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(() => faulted.WaitAsync(Watchdog.Interval));

        error.Message.ShouldNotContain("127.0.0.1");
        fixture.Requests.ShouldBe(0, "evidence for the wrong shard must never be fetched");
        fixture.Refresher.Current.ShouldBeSameAs(start);
        await AssertReadyStatusAsync(fixture, HealthStatus.Unhealthy);
    }
}

using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Polly.CircuitBreaker;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The per-shard circuit breaker (design spec §8): what opens it, what it spares, how it recovers, and that metadata
/// refresh bypasses it. Breaker failures are produced with POST searches, which are never retried, so a manual clock
/// is never waited on for a retry backoff.
/// </summary>
public sealed class HttpShardClientBreakerTests
{
    private const string SecretBody = "Sarah synthetic-token internal detail";

    private static readonly TimeSpan PastTheBreak = ShardResilience.BreakDuration + TimeSpan.FromSeconds(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnoughTransientFailures_OpenTheCircuit_AndNothingIsSentOrMeteredWhileItIsOpen()
    {
        await using var shard = ShardHarness.Start();
        await TripAsync(shard);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.CircuitOpen);
        failure.StatusCode.ShouldBeNull();
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("Sarah");
        failure.Message.ShouldNotContain(SyntheticCredentials.Token);
        failure.Message.ShouldNotContain("127.0.0.1");
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
        shard.Meter.Sends.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task OneFewerFailureThanTheThroughput_LeavesTheCircuitClosed()
    {
        await using var shard = ShardHarness.Start();
        await TripAsync(shard, ShardResilience.MinimumThroughput - 1);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task OpenCircuit_AppliesToEveryOperation()
    {
        await using var shard = ShardHarness.Start();
        await TripAsync(shard);

        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        await AssertCircuitOpenAsync(() => shard.Client.ContinueAsync(shard.Cursor("?name=Sarah&_count=10&ct=Y3Vyc29y"), Ct));
        await AssertCircuitOpenAsync(() => shard.Client.ReadAsync(new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("p1")), Ct));

        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task Timeouts_CountAgainstTheBackend()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        var deadline = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: timeout, clock: new ManualTimeProvider(), deadlineClock: deadline);
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            var response = new ResponseHold();
            shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(), response.BeforeHead));
            Task<ShardPage> call = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
            await response.ArrivalAsync(Ct);
            deadline.Advance(timeout);

            ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(call));
            failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        }

        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task ConnectionsClosedWithoutAnAnswer_CountAgainstTheBackend()
    {
        await using var shard = ShardHarness.Start();
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            shard.Server.Respond(ScriptedResponse.Hangup());
            ShardCallException hangup = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
            hangup.Failure.ShouldBe(ShardCallFailure.Transport);
        }

        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
    }

    [Fact]
    public async Task RetriedAttempts_EachCountAndAGetRetryMeetingTheOpenedCircuitReportsItOpen()
    {
        await using var shard = ShardHarness.Start();
        for (int i = 0; i < 2; i++)
        {
            shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Status(503));
            ShardCallException refused = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
            refused.StatusCode.ShouldBe(503);
        }

        // The fifth attempt is this call's first; its retry then finds the circuit open.
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));
        ShardCallException open = await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        // This call's first attempt did send, so the failure must not claim that nothing was sent.
        open.Message.ShouldNotContain("nothing was sent");
        shard.Server.Requests.Count.ShouldBe(5);
        shard.Meter.Sends.Count.ShouldBe(5);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task DefinitiveRefusals_SayTheBackendIsAlive_AndNeverOpenTheCircuit(int status)
    {
        await using var shard = ShardHarness.Start();
        for (int i = 0; i < ShardResilience.MinimumThroughput * 2; i++)
        {
            shard.Server.Respond(ScriptedResponse.Status(status));
            await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Fact]
    public async Task MalformedAndOversizedAnswers_SayTheBackendIsAlive_AndNeverOpenTheCircuit()
    {
        await using var shard = ShardHarness.Start(maxResponseBytes: 400);
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            shard.Server.Respond(ScriptedResponse.Json("not json"));
            (await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)))
                .Failure.ShouldBe(ShardCallFailure.MalformedResponse);
            shard.Server.Respond(ScriptedResponse.Bytes(new byte[1000]));
            (await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)))
                .Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Fact]
    public async Task MeterAndCredentialExceptions_AreNotTheBackendsFailures()
    {
        await using var shard = ShardHarness.Start();
        var exhausted = new InvalidOperationException("synthetic budget exhausted");
        var unavailable = new HttpRequestException("synthetic identity endpoint unavailable");
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            shard.Meter.ThrowOnSend = exhausted;
            (await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct))).ShouldBeSameAs(exhausted);
            shard.Meter.ThrowOnSend = null;
            shard.Credentials.Acquire = _ => ValueTask.FromException<string>(unavailable);
            (await Should.ThrowAsync<HttpRequestException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct))).ShouldBeSameAs(unavailable);
            shard.Credentials.Acquire = _ => ValueTask.FromResult(SyntheticCredentials.Token);
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Fact]
    public async Task CallerCancellation_IsNotTheBackendsFailure()
    {
        await using var shard = ShardHarness.Start();
        for (int i = 0; i < ShardResilience.MinimumThroughput + 1; i++)
        {
            shard.Server.Respond(ScriptedResponse.Stall());
            await CancelAStalledCallAsync(shard);
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    private static async Task CancelAStalledCallAsync(ShardHarness shard)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int before = shard.Server.Requests.Count;
        Task<ShardPage> call = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), caller.Token);
        await WaitForRequestsAsync(shard, before + 1);
        await caller.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => Guard(call));
    }

    [Fact]
    public async Task BrokenCircuitExceptionThrownByTheMeter_KeepsItsIdentityAndIsNotTheBreakersOwn()
    {
        await using var shard = ShardHarness.Start();
        var fromTheMeter = new BrokenCircuitException("synthetic meter circuit");
        shard.Meter.ThrowOnSend = fromTheMeter;

        (await Should.ThrowAsync<BrokenCircuitException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct))).ShouldBeSameAs(fromTheMeter);
    }

    [Fact]
    public async Task MetadataRefresh_BypassesAnOpenCircuit()
    {
        await using var shard = ShardHarness.Start();
        await TripAsync(shard);
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
        failure.StatusCode.ShouldBe(503);
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput + 1);
        shard.Server.Requests[^1].Target.ShouldBe("/fhir/metadata");
    }

    [Fact]
    public async Task MetadataFailures_NeverOpenTheCircuit()
    {
        await using var shard = ShardHarness.Start();
        for (int i = 0; i < ShardResilience.MinimumThroughput * 2; i++)
        {
            shard.Server.Respond(ScriptedResponse.Status(503));
            await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Fact]
    public async Task TheCircuit_IsSharedByClientsOfOneShard_AndEachKeepsItsOwnMeter()
    {
        await using var shard = ShardHarness.Start();
        var otherMeter = new RecordingMeter();
        HttpShardClient other = shard.AddClient(otherMeter);
        await TripAsync(shard);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        await AssertCircuitOpenAsync(() => other.SearchAsync(shard.SarahRequest(), Ct));

        otherMeter.Sends.ShouldBeEmpty();
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task TheCircuit_IsPerShard()
    {
        await using var east = ShardHarness.Start(id: "east");
        await using var west = ShardHarness.Start(id: "west");
        await TripAsync(east);
        west.Server.Respond(ScriptedResponse.Json(west.SarahPage()));

        await west.Client.SearchAsync(west.SarahRequest(), Ct);

        west.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AfterTheBreakPeriod_OneProbeIsLetThrough_AndASuccessClosesTheCircuit()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        clock.Advance(ShardResilience.BreakDuration - TimeSpan.FromSeconds(1));
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        clock.Advance(TimeSpan.FromSeconds(2));
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()), ScriptedResponse.Json(shard.SarahPage()), ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput + 3);
    }

    [Fact]
    public async Task AFailedProbe_ReopensTheCircuitForAnotherBreakPeriod()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        shard.Server.Respond(ScriptedResponse.Status(503));

        ShardCallException probe = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));

        probe.StatusCode.ShouldBe(503);
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput + 1);
        clock.Advance(PastTheBreak);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyTheAdmittedProbe_DecidesRecovery_WhateverAnAttemptFromBeforeTheBreakReports(bool probeSucceeds)
    {
        // The early attempt ends the opposite way to the probe, so whichever of the two decided recovery is visible.
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMinutes(1), clock: clock);
        var early = new ResponseHold();
        var probe = new ResponseHold();
        shard.Server.Respond(Held(shard, succeeds: !probeSucceeds, early));
        Task<ShardPage> earlyCall = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
        await early.ArrivalAsync(Ct);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        shard.Server.Respond(Held(shard, probeSucceeds, probe));
        Task<ShardPage> probeCall = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
        await probe.ArrivalAsync(Ct);

        early.Release();
        await SettlesAsync(earlyCall, succeeds: !probeSucceeds);

        // The probe is still in flight, so the circuit still fails fast and nothing else reaches the wire.
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        int sent = shard.Server.Requests.Count;
        int acquired = shard.Credentials.Calls;
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        shard.Server.Requests.Count.ShouldBe(sent);
        shard.Meter.Sends.Count.ShouldBe(sent);
        shard.Credentials.Calls.ShouldBe(acquired);

        probe.Release();
        await SettlesAsync(probeCall, probeSucceeds);

        if (probeSucceeds)
        {
            await Guard(shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
            shard.Server.Requests.Count.ShouldBe(sent + 1);
        }
        else
        {
            await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
            shard.Server.Requests.Count.ShouldBe(sent);
        }
    }

    [Fact]
    public async Task AProbeTheCallerCancels_ProvesNothing_AndLeavesTheCircuitOpen()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        shard.Server.Respond(ScriptedResponse.Stall());
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<ShardPage> probe = shard.Client.SearchAsync(shard.SarahRequest(), caller.Token);
        await WaitForRequestsAsync(shard, ShardResilience.MinimumThroughput + 1);
        await caller.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        clock.Advance(PastTheBreak);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
    }

    [Fact]
    public async Task AProbeTheMeterRefuses_ProvesNothing_AndLeavesTheCircuitOpen()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        var exhausted = new InvalidOperationException("synthetic budget exhausted");
        shard.Meter.ThrowOnSend = exhausted;

        (await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(exhausted);

        shard.Meter.ThrowOnSend = null;
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task OpenCircuit_AcquiresNoCredential_SoABlockedProviderCannotDelayTheRejection()
    {
        await using var shard = ShardHarness.Start();
        await TripAsync(shard);
        int acquired = shard.Credentials.Calls;
        shard.Credentials.Acquire = NeverAnswers;

        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        await AssertCircuitOpenAsync(() => shard.Client.ReadAsync(new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("p1")), Ct));

        shard.Credentials.Calls.ShouldBe(acquired);
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task ACredentialTheDeadlineCutsShort_IsATimeoutButNotTheBackendsFailure()
    {
        TimeSpan timeout = TimeSpan.FromSeconds(10);
        var deadline = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: timeout, clock: new ManualTimeProvider(), deadlineClock: deadline);
        for (int i = 0; i < ShardResilience.MinimumThroughput + 1; i++)
        {
            var acquiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shard.Credentials.Acquire = ct =>
            {
                acquiring.SetResult();
                return NeverAnswers(ct);
            };
            Task<ShardPage> call = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
            await acquiring.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
            deadline.Advance(timeout);

            ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(call));
            failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        }

        // The success call uses the same breaker; only Guard bounds it in wall-clock time.
        shard.Credentials.Acquire = _ => ValueTask.FromResult(SyntheticCredentials.Token);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        shard.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task NeutralOutcomes_AreRecordedAsSuccesses_SoTheyDiluteTheFailureRatio()
    {
        await using var shard = ShardHarness.Start();
        var exhausted = new InvalidOperationException("synthetic budget exhausted");
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            // Two neutral outcomes between failures keep five failures under half of the sample.
            shard.Meter.ThrowOnSend = exhausted;
            for (int neutral = 0; neutral < 2; neutral++)
            {
                await Should.ThrowAsync<InvalidOperationException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)));
            }

            shard.Meter.ThrowOnSend = null;
            shard.Server.Respond(ScriptedResponse.Status(503));
            await Should.ThrowAsync<ShardCallException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)));
        }

        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));
    }

    [Fact]
    public async Task ShardCallExceptionThrownByTheMeter_KeepsItsIdentityAndProvesNothingToAProbe()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        var fromTheMeter = new ShardCallException("synthetic meter wrapper");
        shard.Meter.ThrowOnSend = fromTheMeter;

        (await Should.ThrowAsync<ShardCallException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct)))).ShouldBeSameAs(fromTheMeter);

        shard.Meter.ThrowOnSend = null;
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task BrokenCircuitExceptionThrownByTheMeterDuringAProbe_KeepsItsIdentity()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        var fromTheMeter = new BrokenCircuitException("synthetic meter circuit");
        shard.Meter.ThrowOnSend = fromTheMeter;

        (await Should.ThrowAsync<BrokenCircuitException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct)))).ShouldBeSameAs(fromTheMeter);

        shard.Meter.ThrowOnSend = null;
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
    }

    [Fact]
    public async Task AProbeWhoseCredentialFails_ProvesNothing_AndLeavesTheCircuitOpen()
    {
        var clock = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        clock.Advance(PastTheBreak);
        var unavailable = new HttpRequestException("synthetic identity endpoint unavailable");
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(unavailable);

        (await Should.ThrowAsync<HttpRequestException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct)))).ShouldBeSameAs(unavailable);

        shard.Credentials.Acquire = _ => ValueTask.FromResult(SyntheticCredentials.Token);
        await AssertCircuitOpenAsync(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task CallerCancellationObservedAsTheBreakerRejects_WinsOverTheCircuitOpenFailure()
    {
        var clock = new HookedClock();
        await using var shard = ShardHarness.Start(clock: clock);
        await TripAsync(shard);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        clock.OnNextRead(caller.Cancel);

        await Should.ThrowAsync<OperationCanceledException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(), caller.Token)));

        shard.Server.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
        shard.Meter.Sends.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task ResilienceForAnotherShard_IsRefusedAtConstruction()
    {
        await using var east = ShardHarness.Start(id: "east");
        await using var west = ShardHarness.Start(id: "west");

        Should.Throw<ArgumentException>(() => new HttpShardClient(east.Shard, east.Credentials, TimeSpan.FromSeconds(1), 1024, SyntheticLimits.Generous, null, west.Resilience));
        Should.Throw<ArgumentNullException>(() => new HttpShardClient(east.Shard, east.Credentials, TimeSpan.FromSeconds(1), 1024, SyntheticLimits.Generous, null, resilience: null!));
        Should.Throw<ArgumentNullException>(() => new ShardResilience(null!));
    }

    [Fact]
    public async Task AReusedShardIdDoesNotBindAClientToAnOldDescriptor()
    {
        await using var earlier = ShardHarness.Start(id: "east");
        await using var current = ShardHarness.Start(id: "east");

        Should.Throw<ArgumentException>(() => new HttpShardClient(current.Shard, current.Credentials, TimeSpan.FromSeconds(1), 1024, SyntheticLimits.Generous, null, earlier.Resilience));
    }

    private static async Task TripAsync(ShardHarness shard, int failures = ShardResilience.MinimumThroughput)
    {
        for (int i = 0; i < failures; i++)
        {
            shard.Server.Respond(ScriptedResponse.Status(503, SecretBody));
            ShardCallException refused = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
            refused.Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
            refused.StatusCode.ShouldBe(503);
        }
    }

    private static async Task<ShardCallException> AssertCircuitOpenAsync<T>(Func<Task<T>> call)
    {
        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(call()));
        failure.Failure.ShouldBe(ShardCallFailure.CircuitOpen);
        failure.StatusCode.ShouldBeNull();
        return failure;
    }

    // Every await on a call is bounded, so a regression fails the test rather than hanging the run.
    private static Task<T> Guard<T>(Task<T> call) => call.WaitAsync(TimeSpan.FromSeconds(15), Ct);

    // A held answer for a POST Sarah search: the page, or a 503 that counts against the backend.
    private static ScriptedResponse Held(ShardHarness shard, bool succeeds, ResponseHold hold) =>
        succeeds ? ScriptedResponse.Json(shard.SarahPage(), hold.BeforeHead) : ScriptedResponse.Status(503, SecretBody, hold.BeforeHead);

    private static async Task SettlesAsync(Task<ShardPage> call, bool succeeds)
    {
        if (succeeds)
        {
            await Guard(call);
        }
        else
        {
            (await Should.ThrowAsync<ShardCallException>(() => Guard(call))).StatusCode.ShouldBe(503);
        }
    }

    private static async Task WaitForRequestsAsync(ShardHarness shard, int count)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromSeconds(15));
        while (shard.Server.Requests.Count < count)
        {
            await Task.Delay(1, guard.Token);
        }
    }

    // Credentials that never answer and ignore cancellation, so only the client's own bound can end the wait.
    private static ValueTask<string> NeverAnswers(CancellationToken _) => new(new TaskCompletionSource<string>().Task);

    // A clock that runs a hook the first time the breaker reads it: after the call has entered it, before it decides.
    private sealed class HookedClock : TimeProvider
    {
        private Action? _onRead;

        public void OnNextRead(Action hook) => _onRead = hook;

        public override DateTimeOffset GetUtcNow()
        {
            Action? hook = Interlocked.Exchange(ref _onRead, null);
            hook?.Invoke();
            return base.GetUtcNow();
        }
    }
}

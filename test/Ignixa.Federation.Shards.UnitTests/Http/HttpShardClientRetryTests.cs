using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The one retry a read or search gets (design spec §8): only a GET, only for a transient status or wire fault, once, within
/// the call's single deadline, and metered per actual send. Observed at real TLS loopback servers.
/// </summary>
public sealed class HttpShardClientRetryTests
{
    private const string SecretBody = "Sarah synthetic-token internal detail";

    private const string NextQuery = "?name=Sarah&_count=10&ct=Y3Vyc29y";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetSearch503ThenOk_RetriesOnceAndMetersBothSends()
    {
        await using var shard = ShardHarness.Start();
        string page = shard.SarahPage(null, null, Searchsets.Entry(Searchsets.Patient("p1")));
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Json(page));

        ShardPage result = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        result.Entries.ShouldHaveSingleItem();
        shard.Server.Requests.Select(r => (r.Method, r.Target)).ShouldBe([("GET", "/fhir/Patient?name=Sarah&_count=10"), ("GET", "/fhir/Patient?name=Sarah&_count=10")]);
        shard.Meter.Sends.Count.ShouldBe(2);
        shard.Meter.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(page));
        shard.Credentials.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task RetriedAttempt_CarriesTheSameHeadersAndAFreshExactRequest()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        IReadOnlyList<RecordedRequest> requests = shard.Server.Requests;
        requests.Count.ShouldBe(2);
        foreach (RecordedRequest request in requests)
        {
            request.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
            request.HeaderValues("Accept").ShouldBe(["application/fhir+json"]);
            request.HeaderValues("Prefer").ShouldBe(["handling=strict"]);
        }
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientStatus_IsRetriedOnce(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(status, SecretBody), ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Get503Twice_StopsAfterOneRetryAndReportsTheBackendStatus()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
        failure.StatusCode.ShouldBe(503);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("internal detail");
        shard.Server.Requests.Count.ShouldBe(2);
        shard.Meter.Sends.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(201)]
    [InlineData(302)]
    public async Task PermanentStatus_IsNeverRetried(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            status == 302 ? ScriptedResponse.Redirect(shard.Search + "?moved=1") : ScriptedResponse.Status(status, SecretBody),
            ScriptedResponse.Json(shard.SarahPage()));

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        shard.Server.Requests.Count.ShouldBe(1);
        shard.Meter.Sends.Count.ShouldBe(1);
        shard.Credentials.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task PostSearch503_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));

        failure.StatusCode.ShouldBe(503);
        shard.Server.Requests.ShouldHaveSingleItem().Method.ShouldBe("POST");
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CursorRedeemedByGet_IsRetriedEvenWhenTheSearchWasAPost()
    {
        // The search that produced this cursor was a POST; redeeming its next link follows it with a GET.
        await using var shard = ShardHarness.Start(ShardHarness.Follow);
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(Searchsets.Page(self: null)));

        await shard.Client.ContinueAsync(shard.Cursor(NextQuery, NextLinkRedemption.FollowGet), Ct);

        shard.Server.Requests.Select(r => r.Method).ShouldBe(["GET", "GET"]);
        shard.Meter.Sends.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CursorRedeemedByPost_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start(ShardHarness.Repost);
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(Searchsets.Page(self: null)));

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.ContinueAsync(shard.Cursor(NextQuery, NextLinkRedemption.RepostToSearch), Ct));

        RecordedRequest request = shard.Server.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Target.ShouldBe("/fhir/Patient/_search");
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Read503ThenOk_RetriesOnce()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"));

        ShardReadResult result = await shard.Client.ReadAsync(Key(shard, "p1"), Ct);

        result.Resource.Length.ShouldBeGreaterThan(0);
        shard.Server.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient/p1", "/fhir/Patient/p1"]);
        shard.Meter.Sends.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task ReadOfAnAbsentResource_IsNeverRetried(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(status, SecretBody), ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"));

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MetadataFetch_IsASingleExchangeThatNeverRetries()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody), ScriptedResponse.Json("{}"));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.StatusCode.ShouldBe(503);
        shard.Server.Requests.Count.ShouldBe(1);
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MalformedPage_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json("not json"), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DroppedPredicate_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start();
        string dropped = Searchsets.Page(shard.Search + "?_count=10");
        shard.Server.Respond(ScriptedResponse.Json(dropped), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.SelfLinkMismatch);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OversizedBody_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start(maxResponseBytes: 4096);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage() + new string(' ', 8192)), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    // SocketsHttpHandler (.NET 11.0.0 HttpConnection/HttpConnectionPool) re-sends, up to three times and without the client
    // seeing it, a request whose connection closed before any response byte when it has no content or expects 100-continue.
    // A GET therefore carries explicit empty content, which makes that path unavailable; the client's own single retry is
    // then the only one, and each of its sends is metered.
    [Fact]
    public async Task GetConnectionClosedWithoutAnAnswer_IsRetriedOnceByTheClientAndEverySendIsMetered()
    {
        await using var shard = ShardHarness.Start();
        string page = shard.SarahPage(null, null, Searchsets.Entry(Searchsets.Patient("p1")));
        shard.Server.Respond(ScriptedResponse.Hangup(), ScriptedResponse.Json(page));

        ShardPage result = await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        result.Entries.ShouldHaveSingleItem();
        shard.Server.Requests.Count.ShouldBe(2);
        shard.Meter.Sends.Count.ShouldBe(2);
        shard.Credentials.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetConnectionClosedTwice_FailsAsTransportAfterExactlyTwoSendsWhateverIsQueued()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Hangup(), ScriptedResponse.Hangup(), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct)));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        failure.InnerException.ShouldBeNull();
        shard.Server.Requests.Count.ShouldBe(2);
        shard.Meter.Sends.Count.ShouldBe(2);
    }

    [Fact]
    public async Task MetadataConnectionClosedWithoutAnAnswer_IsOneSendAndNeverReplayed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Hangup(), ScriptedResponse.Json("{}"));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(shard.Client.GetCapabilitiesAsync(Ct)));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        shard.Server.Requests.Count.ShouldBe(1);
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task PostConnectionClosedWithoutAnAnswer_IsNeverRetriedOrReplayed()
    {
        // A POST has content and does not expect 100-continue, so the handler does not re-send it; the client never retries it.
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Hangup(), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => Guard(shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        failure.InnerException.ShouldBeNull();
        shard.Server.Requests.ShouldHaveSingleItem().Body.ShouldNotBeEmpty();
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task EveryGet_FramesAnEmptyBody_WithoutExpectContinueOrPayloadBytes()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ScriptedResponse.Json(shard.SarahPage()),
            ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"),
            ScriptedResponse.Json(Searchsets.Page(self: null)),
            ScriptedResponse.Json("{}"));

        await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        await Guard(shard.Client.ReadAsync(Key(shard, "p1"), Ct));
        await Guard(shard.Client.ContinueAsync(shard.Cursor(NextQuery), Ct));
        await Should.ThrowAsync<ShardCallException>(() => Guard(shard.Client.GetCapabilitiesAsync(Ct)));

        IReadOnlyList<RecordedRequest> requests = shard.Server.Requests;
        requests.Select(r => r.Method).ShouldBe(["GET", "GET", "GET", "GET"]);
        foreach (RecordedRequest request in requests)
        {
            request.HeaderValues("Content-Length").ShouldBe(["0"]);
            request.Body.ShouldBeEmpty();
            request.HeaderValues("Expect").ShouldBeEmpty();
            request.HeaderValues("Transfer-Encoding").ShouldBeEmpty();
            request.HeaderValues("Content-Type").ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task BodyTruncatedOnTheWire_IsRetriedOnceAndMeterCountsEveryByteConsumed()
    {
        await using var shard = ShardHarness.Start();
        string page = shard.SarahPage(null, null, Searchsets.Entry(Searchsets.Patient("p1")));
        ScriptedResponse truncated = ScriptedResponse.Json(page).TruncatingBody();
        shard.Server.Respond(truncated, ScriptedResponse.Json(page));

        ShardPage result = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        result.Entries.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(2);
        shard.Meter.Bytes.ShouldBe(2L * Encoding.UTF8.GetByteCount(page));
    }

    [Fact]
    public async Task UntrustedCertificate_IsPermanentAndNeverRetried()
    {
        await using var shard = ShardHarness.Start(trustLoopback: false);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        shard.Meter.Sends.Count.ShouldBe(1);
        shard.Credentials.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task CredentialFailure_IsNeverRetriedAndPropagatesUnchanged()
    {
        await using var shard = ShardHarness.Start();
        var unavailable = new HttpRequestException("synthetic identity endpoint unavailable");
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(unavailable);

        (await Should.ThrowAsync<HttpRequestException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(unavailable);

        shard.Credentials.Calls.ShouldBe(1);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task MeterDenyingTheRetry_PropagatesUnchangedWithoutASecondPhysicalSend()
    {
        await using var shard = ShardHarness.Start();
        var exhausted = new InvalidOperationException("synthetic budget exhausted");
        shard.Meter.ThrowOnSend = exhausted;
        shard.Meter.ThrowFromSend = 2;
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));

        (await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(exhausted);

        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AProviderRethrowingAnEarlierBackendFailureDoesNotMakeItRetryable()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503));
        ShardCallException prior = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(prior);

        (await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(prior);

        shard.Credentials.Calls.ShouldBe(2);
        shard.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MeterDenyingTheRetryWithAWireLikeException_IsNotATransportFailure()
    {
        await using var shard = ShardHarness.Start();
        var denial = new IOException("synthetic meter store unavailable");
        shard.Meter.ThrowOnSend = denial;
        shard.Meter.ThrowFromSend = 2;
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));

        (await Should.ThrowAsync<IOException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(denial);

        shard.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task MeterFailingOnBodyBytes_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start();
        var denial = new IOException("synthetic meter store unavailable");
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()), ScriptedResponse.Json(shard.SarahPage()));
        shard.Meter.OnBytes = _ => throw denial;

        (await Should.ThrowAsync<IOException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(denial);

        shard.Server.Requests.Count.ShouldBe(1);
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Timeout_IsNeverRetried()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Stall(), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OneDeadlineCoversBothAttemptsAndTheBackoffBetweenThem()
    {
        var timeout = TimeSpan.FromMilliseconds(1000);
        await using var shard = ShardHarness.Start(callTimeout: timeout);
        shard.Server.Respond(
            ScriptedResponse.Status(503, beforeHead: _ => Task.Delay(TimeSpan.FromMilliseconds(500), Ct)),
            ScriptedResponse.Stall());
        var clock = Stopwatch.StartNew();

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        shard.Server.Requests.Count.ShouldBe(2);
        // A fresh deadline for the retry would take the 500 ms first attempt, the backoff and a whole second more.
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(1600));
    }

    [Fact]
    public async Task TheRetry_WaitsOutItsBackoffOnTheResilienceClock()
    {
        var clock = new BackoffClock();
        await using var shard = ShardHarness.Start(clock: clock);
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));

        Task<ShardPage> call = shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        await clock.Armed.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        // The first attempt has failed and the retry is parked on a timer: nothing else can have reached the server.
        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.ShouldHaveSingleItem();
        call.IsCompleted.ShouldBeFalse();

        clock.Advance(ShardResilience.RetryDelay * 2);
        await Guard(call);

        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CallerCancellingDuringTheBackoff_IsCancellationAndSendsNoSecondRequest()
    {
        var clock = new BackoffClock();
        await using var shard = ShardHarness.Start(clock: clock);
        shard.Server.Respond(ScriptedResponse.Status(503), ScriptedResponse.Json(shard.SarahPage()));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<ShardPage> call = shard.Client.SearchAsync(shard.SarahRequest(), caller.Token);
        await clock.Armed.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await caller.CancelAsync();
        // Even a backoff that then elapses must not send.
        clock.Advance(ShardResilience.RetryDelay * 2);

        await Should.ThrowAsync<OperationCanceledException>(() => Guard(call));
        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.ShouldHaveSingleItem();
    }

    private static ResourceKey Key(ShardHarness shard, string id) => new(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse(id));

    // Every await on a call is bounded, so a regression fails the test rather than hanging the run.
    private static Task<T> Guard<T>(Task<T> call) => call.WaitAsync(TimeSpan.FromSeconds(15), Ct);

    private sealed class BackoffClock : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        private readonly TaskCompletionSource _armed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Armed => _armed.Task;

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = _clock.CreateTimer(callback, state, dueTime, period);
            _armed.TrySetResult();
            return timer;
        }

        public void Advance(TimeSpan by) => _clock.Advance(by);
    }
}

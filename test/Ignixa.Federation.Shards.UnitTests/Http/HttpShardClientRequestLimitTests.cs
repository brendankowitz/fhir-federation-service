using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The per-shard request limits at the real transport: a search or redemption is measured as the exact bytes it would put
/// on the wire (the origin-form target for GET, the form body for POST) and, when it does not fit, nothing is sent,
/// credentialed, metered or traced (design spec §7.8). Observed at a TLS loopback server.
/// </summary>
public sealed class HttpShardClientRequestLimitTests
{
    // "/fhir/Patient?" is 14 bytes and the query 20, so the GET target is 34 bytes and the POST body 20.
    private const string Query = "name=Sarah&_count=10";
    private const string GetTarget = "/fhir/Patient?" + Query;
    private const string PostTarget = "/fhir/Patient/_search";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The other method's ceiling is a single byte, so a test that passes also shows the ceiling is chosen by the actual method.
    private static ShardRequestLimits Only(ShardSearchMethod method, int bytes) =>
        method == ShardSearchMethod.Get ? new(maxGetTargetBytes: bytes, maxPostBodyBytes: 1) : new(maxGetTargetBytes: 1, maxPostBodyBytes: bytes);

    private static ShardRequestLimits Only(NextLinkRedemption redemption, int bytes) =>
        redemption == NextLinkRedemption.FollowGet ? new(maxGetTargetBytes: bytes, maxPostBodyBytes: 1) : new(maxGetTargetBytes: 1, maxPostBodyBytes: bytes);

    private static int WireBytes(ShardSearchMethod method) => method == ShardSearchMethod.Get ? 34 : 20;

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task Search_ExactlyAtTheLimit_IsSentUnchanged(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start(limits: Only(method, WireBytes(method)));
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(method), Ct);

        RecordedRequest sent = shard.Server.Requests.ShouldHaveSingleItem();
        if (method == ShardSearchMethod.Get)
        {
            (sent.Method, sent.Target).ShouldBe(("GET", GetTarget));
        }
        else
        {
            (sent.Method, sent.Target, Encoding.ASCII.GetString(sent.Body)).ShouldBe(("POST", PostTarget, Query));
        }
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task Search_OneByteOverTheLimit_SendsAcquiresMetersAndTracesNothing(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start(limits: Only(method, WireBytes(method) - 1));
        using TraceCapture trace = TraceCapture.Start();

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(method), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        failure.StatusCode.ShouldBeNull();
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("Sarah");
        failure.Message.ShouldNotContain("name");
        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
        shard.Meter.Sends.ShouldBeEmpty();
        trace.Stopped.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task Search_CountsEscapesAndRepeatedOccurrencesAsWritten(ShardSearchMethod method)
    {
        // Decoded, these occurrences would be much shorter; the limit is about the escaped text that is actually sent.
        const string query = "name=%41%2fb&name=%41%2fb&code=a%7Cb&_count=10";
        string[] occurrences = ["name", "%41%2fb", "name", "%41%2fb", "code", "a%7Cb"];
        int wire = method == ShardSearchMethod.Get ? "/fhir/Patient?".Length + query.Length : query.Length;

        await using var exact = ShardHarness.Start(limits: Only(method, wire));
        exact.Server.Respond(ScriptedResponse.Json(Searchsets.Page(exact.Search + "?" + query)));
        await exact.Client.SearchAsync(exact.Request(method, 10, occurrences), Ct);
        await using var under = ShardHarness.Start(limits: Only(method, wire - 1));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => under.Client.SearchAsync(under.Request(method, 10, occurrences), Ct));

        RecordedRequest sent = exact.Server.Requests.ShouldHaveSingleItem();
        (method == ShardSearchMethod.Get ? sent.Target : Encoding.ASCII.GetString(sent.Body))
            .ShouldBe(method == ShardSearchMethod.Get ? "/fhir/Patient?" + query : query);
        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        under.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSearch_CountsTheCanonicalBasePathInTheTarget()
    {
        const string target = "/tenant%2Fa/fhir/Patient?" + Query;
        await using var exact = ShardHarness.Start(basePath: "/tenant%2fa/fhir", limits: Only(ShardSearchMethod.Get, target.Length));
        exact.Server.Respond(ScriptedResponse.Json(exact.SarahPage()));
        await exact.Client.SearchAsync(exact.SarahRequest(), Ct);
        await using var under = ShardHarness.Start(basePath: "/tenant%2fa/fhir", limits: Only(ShardSearchMethod.Get, target.Length - 1));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => under.Client.SearchAsync(under.SarahRequest(), Ct));

        exact.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe(target);
        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        under.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PostSearch_DoesNotCountTheFixedTargetOrTheBasePath()
    {
        await using var shard = ShardHarness.Start(basePath: "/tenant/a/fhir", limits: Only(ShardSearchMethod.PostSearch, Query.Length));
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        await shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);

        RecordedRequest sent = shard.Server.Requests.ShouldHaveSingleItem();
        sent.Target.ShouldBe("/tenant/a/fhir/Patient/_search");
        Encoding.ASCII.GetString(sent.Body).ShouldBe(Query);
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task Search_ThatDoesNotFitItsOwnMethod_IsNeverMovedToTheOtherMethodOrTrimmed(ShardSearchMethod method)
    {
        // The other method's ceiling is generous, so a fallback to it would succeed.
        ShardRequestLimits limits = method == ShardSearchMethod.Get
            ? new(maxGetTargetBytes: GetTarget.Length - 1, maxPostBodyBytes: 1 << 20)
            : new(maxGetTargetBytes: 1 << 20, maxPostBodyBytes: Query.Length - 1);
        await using var shard = ShardHarness.Start(limits: limits);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(method), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        shard.Server.Requests.ShouldBeEmpty();
        shard.Meter.Sends.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(NextLinkRedemption.FollowGet, "{alias}/Patient?ct=Mg")]
    [InlineData(NextLinkRedemption.FollowGet, "Patient?ct=Mg")]
    [InlineData(NextLinkRedemption.FollowGet, "/fhir/Patient?ct=Mg")]
    [InlineData(NextLinkRedemption.FollowGet, "?ct=Mg")]
    [InlineData(NextLinkRedemption.RepostToSearch, "{alias}/Patient?ct=Mg")]
    [InlineData(NextLinkRedemption.RepostToSearch, "Patient?ct=Mg")]
    [InlineData(NextLinkRedemption.RepostToSearch, "?ct=Mg")]
    public async Task Redemption_IsMeasuredOnTheCanonicalWireRequestAtItsOwnMethod(NextLinkRedemption redemption, string form)
    {
        ArgumentNullException.ThrowIfNull(form);

        // Redeemed at the configured base whatever the link's own text says: the target is 19 bytes, the body 5.
        int wire = redemption == NextLinkRedemption.FollowGet ? "/fhir/Patient?ct=Mg".Length : "ct=Mg".Length;
        await using var exact = ShardHarness.Start(limits: Only(redemption, wire));
        string target = form.Replace("{alias}", exact.AliasBase, StringComparison.Ordinal);
        exact.Server.Respond(ScriptedResponse.Json(Searchsets.Page(self: null)));
        await exact.Client.ContinueAsync(exact.Cursor(target, redemption), Ct);
        await using var under = ShardHarness.Start(limits: Only(redemption, wire - 1));
        string underTarget = form.Replace("{alias}", under.AliasBase, StringComparison.Ordinal);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => under.Client.ContinueAsync(under.Cursor(underTarget, redemption), Ct));

        RecordedRequest sent = exact.Server.Requests.ShouldHaveSingleItem();
        if (redemption == NextLinkRedemption.FollowGet)
        {
            (sent.Method, sent.Target).ShouldBe(("GET", "/fhir/Patient?ct=Mg"));
        }
        else
        {
            (sent.Method, sent.Target, Encoding.ASCII.GetString(sent.Body)).ShouldBe(("POST", PostTarget, "ct=Mg"));
        }

        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
        failure.StatusCode.ShouldBeNull();
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("ct=");
        failure.Message.ShouldNotContain("Mg");
        under.Server.Requests.ShouldBeEmpty();
        under.Credentials.Calls.ShouldBe(0);
        under.Meter.Sends.ShouldBeEmpty();
    }

    [Fact]
    public async Task NextLinkOverTheLimit_IsReturnedWithItsPageAndFailsOnlyWhenRedeemed()
    {
        string next = "?ct=" + new string('x', 60);
        await using var shard = ShardHarness.Start(ShardHarness.Follow, limits: Only(ShardSearchMethod.Get, GetTarget.Length));
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(next: next)));

        ShardPage first = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ContinueAsync(first.Next!, Ct));

        first.Next!.Target.ShouldBe(next);
        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe(GetTarget);
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ForeignCursor_StaysAnArgumentErrorEvenWhenTheLimitsAreTiny()
    {
        await using var shard = ShardHarness.Start(limits: new ShardRequestLimits(1, 1));

        Should.Throw<ArgumentException>(() => { _ = shard.Client.ContinueAsync(shard.Cursor("https://other.example/fhir/Patient?ct=Mg"), Ct); });
        Should.Throw<ArgumentException>(() => { _ = shard.Client.ContinueAsync(new ShardCursor(ShardId.Parse("west"), ShardHarness.Patient, "?ct=Mg", NextLinkRedemption.FollowGet), Ct); });

        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task LocalRejections_AreNotBackendFaultsAndNeverOpenTheCircuit()
    {
        await using var shard = ShardHarness.Start(limits: Only(ShardSearchMethod.Get, "/fhir/Patient?_count=10".Length));
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?_count=10")));
        for (int i = 0; i < 2 * ShardResilience.MinimumThroughput; i++)
        {
            await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));
        }

        await shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 10), Ct);

        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient?_count=10");
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CancelledCall_WithAnOversizeRequest_ReportsCancellation()
    {
        await using var shard = ShardHarness.Start(limits: Only(ShardSearchMethod.Get, 1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.SearchAsync(shard.SarahRequest(), cancelled.Token));

        shard.Server.Requests.ShouldBeEmpty();
    }

    // Collects the spans the client stops, so a test can show that a rejected request never started one.
    private sealed class TraceCapture : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Activity _parent = new("synthetic-test-parent");

        private TraceCapture()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "Ignixa.Federation",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = a =>
                {
                    if (a.TraceId == _parent.TraceId)
                    {
                        lock (Stopped)
                        {
                            Stopped.Add(a);
                        }
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
            _parent.Start();
        }

        public List<Activity> Stopped { get; } = [];

        public static TraceCapture Start() => new();

        public void Dispose()
        {
            _parent.Dispose();
            _listener.Dispose();
        }
    }
}

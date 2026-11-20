using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// The <c>_sort=_lastUpdated&amp;_count=1</c> and replay probe as a caller sees it, over a real TLS loopback transport:
/// what it sends, and which observations it reports as shown (design spec §7.3, §7.4, §9.2).
/// </summary>
public sealed class LastUpdatedReplayProbeTests
{
    private const string Sort = "_sort=_lastUpdated&_count=1";
    private const string FirstTarget = "/fhir/Patient?" + Sort;
    private const string RedeemedTarget = FirstTarget + "&ct=A";
    private const string Early = "2026-01-02T03:00:00Z";
    private const string Late = "2026-01-02T03:30:00Z";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ShardSearchMethod, bool, string, string> Streams => new()
    {
        // method, profile reposts, first request, each redemption
        { ShardSearchMethod.Get, false, "GET " + FirstTarget, "GET " + RedeemedTarget },
        { ShardSearchMethod.Get, true, "GET " + FirstTarget, "GET " + RedeemedTarget },
        { ShardSearchMethod.PostSearch, false, "POST /fhir/Patient/_search " + Sort, "GET " + RedeemedTarget },
        { ShardSearchMethod.PostSearch, true, "POST /fhir/Patient/_search " + Sort, "POST /fhir/Patient/_search " + Sort + "&ct=A" },
    };

    private static Task<LastUpdatedReplayProbeResult> ProbeAsync(ShardHarness shard, ShardSearchMethod method = ShardSearchMethod.Get) =>
        LastUpdatedReplayProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, method, Ct);

    private static string Entry(string id, string version = "1", string at = Early) => Searchsets.Entry(Searchsets.Patient(id, version, at));

    private static string Next(ShardHarness shard, string token = "A") => $"{shard.Search}?{Sort}&ct={token}";

    private static ScriptedResponse First(ShardHarness shard, string? next, params string[] entries) =>
        ScriptedResponse.Json(Searchsets.Page($"{shard.Search}?{Sort}", next, null, entries));

    private static ScriptedResponse Redeemed(string? next, params string[] entries) =>
        ScriptedResponse.Json(Searchsets.Page(null, next, null, entries));

    private static void Script(ShardHarness shard, string[] first, string[] second, string[]? replay = null) =>
        shard.Server.Respond(First(shard, Next(shard), first), Redeemed(Next(shard, "B"), second), Redeemed(Next(shard, "C"), replay ?? second));

    private static string Wire(RecordedRequest request) =>
        request.Method == "POST"
            ? $"POST {request.Target} {Encoding.ASCII.GetString(request.Body)}"
            : $"{request.Method} {request.Target}";

    [Theory]
    [MemberData(nameof(Streams))]
    public async Task ObservedBoundary_IsReportedForTheMethodThatStartedAndRedeemedIt(
        ShardSearchMethod method, bool repost, string firstWire, string redeemWire)
    {
        await using var shard = ShardHarness.Start(repost ? ShardHarness.Repost : ShardHarness.Follow);
        Script(shard, [Entry("a1")], [Entry("b1", at: Late)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard, method);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.Observed);
        result.IsObserved.ShouldBeTrue();
        result.Shard.ShouldBe(shard.Shard.Id);
        result.Type.ShouldBe(ShardHarness.Patient);
        result.StartMethod.ShouldBe(method);
        result.Redemption.ShouldBe(repost && method == ShardSearchMethod.PostSearch ? NextLinkRedemption.RepostToSearch : NextLinkRedemption.FollowGet);
        // One search and the same cursor redeemed twice; the second page's own next (B, C) is never followed, and no metadata is fetched.
        shard.Server.Requests.Select(Wire).ShouldBe([firstWire, redeemWire, redeemWire]);
        foreach (RecordedRequest sent in shard.Server.Requests)
        {
            sent.HeaderValues("Prefer").ShouldBe(["handling=strict"]);
            sent.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
        }

        shard.Meter.Sends.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("2026-01-02T05:00:00+02:00", "2026-01-02T02:30:00-01:00")] // 03:00Z then 03:30Z; later text sorts earlier
    [InlineData("2026-01-02T03:00:00.0000001Z", "2026-01-02T03:00:00.0000002Z")]
    [InlineData("2026-01-02T03:00:00Z", "2026-01-02T05:00:00+02:00")] // the same instant: an observed tie
    public async Task Boundary_WhoseInstantsAreNondecreasingAsInstants_IsObserved(string first, string next)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1", at: first)], [Entry("b1", at: next)]);

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.Observed);
    }

    [Theory]
    [InlineData("2026-01-02T02:30:00-01:00", "2026-01-02T05:00:00+02:00")] // 03:30Z then 03:00Z; the text sorts ascending
    [InlineData("2026-01-02T03:00:00.0000002Z", "2026-01-02T03:00:00.0000001Z")]
    public async Task Boundary_WhoseNextInstantIsEarlier_IsNotMonotone_AndTheReplayIsNotRead(string first, string next)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1", at: first)], [Entry("b1", at: next)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.OrderNotMonotone);
        result.IsObserved.ShouldBeFalse();
        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task NextEntryThatIsTheSameResourceAsTheFirst_IsNotADistinctBoundary_EvenWithAnotherVersion()
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1", "1")], [Entry("a1", "2", Late)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.BoundaryNotDistinct);
        shard.Server.Requests.Count.ShouldBe(2);
    }

    private const string NoInstantA = """{"resourceType":"Patient","id":"a1","meta":{"versionId":"1"}}""";
    private const string NoMetaA = """{"resourceType":"Patient","id":"a1"}""";
    private const string NoInstantB = """{"resourceType":"Patient","id":"b1","meta":{"versionId":"1"}}""";

    public static TheoryData<string, string, int> MissingInstants => new()
    {
        { Searchsets.Entry(NoInstantA), Entry("b1", at: Late), 1 },
        { Searchsets.Entry(NoMetaA), Entry("b1", at: Late), 1 },
        { Entry("a1"), Searchsets.Entry(NoInstantB), 2 },
    };

    [Theory]
    [MemberData(nameof(MissingInstants))]
    public async Task EntryWithoutAnInstant_IsNeverOrderEvidence(string first, string next, int expectedRequests)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [first], [next]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.InstantMissing);
        shard.Server.Requests.Count.ShouldBe(expectedRequests);
    }

    [Theory]
    [InlineData("2026-01-02")]
    [InlineData("2026-01-02T03:00:00")] // a FHIR instant has a zone
    [InlineData("2026-01-02T03:00:00.12345678Z")] // finer than a DateTimeOffset can hold
    public async Task EntryWithAnInstantTheTransportRefuses_IsAFailedRead(string instant)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1", at: instant)], [Entry("b1", at: Late)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    public static TheoryData<string[], string[], int> InsufficientPages => new()
    {
        { [], [Entry("b1", at: Late)], 1 }, // an empty first page, though it has a next link
        { [Entry("a1"), Entry("a2")], [Entry("b1", at: Late)], 1 }, // more than _count=1 matches
        { [Entry("a1")], [], 2 }, // the first redemption is empty
        { [Entry("a1")], [Entry("b1", at: Late), Entry("b2", at: Late)], 2 },
    };

    [Theory]
    [MemberData(nameof(InsufficientPages))]
    public async Task PageWithoutExactlyOneMatch_IsInsufficient(string[] first, string[] second, int expectedRequests)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, first, second);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.InsufficientRows);
        result.IsObserved.ShouldBeFalse();
        shard.Server.Requests.Count.ShouldBe(expectedRequests);
    }

    [Fact]
    public async Task SecondRedemptionWithoutExactlyOneMatch_IsInsufficient()
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1")], [Entry("b1", at: Late)], replay: []);

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.InsufficientRows);
        shard.Server.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FirstPageWithoutANext_IsInsufficient_AndNothingIsInventedToRedeem()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(First(shard, next: null, Entry("a1")));

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.InsufficientRows);
        result.Redemption.ShouldBeNull();
        shard.Server.Requests.Select(Wire).ShouldBe(["GET " + FirstTarget]);
    }

    [Fact]
    public async Task RedemptionPageWithoutANext_StillObserves_BecauseOnlyTheFirstPagesNextIsRedeemed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(First(shard, Next(shard), Entry("a1")), Redeemed(null, Entry("b1", at: Late)), Redeemed(null, Entry("b1", at: Late)));

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.Observed);
    }

    private static readonly string Plain = Searchsets.Patient("b1", "1", Late);

    public static TheoryData<string> ChangedReplays => new()
    {
        Searchsets.Patient("b2", "1", Late), // another identity
        Searchsets.Patient("b1", "2", Late), // another version
        Searchsets.Patient("b1", "1", "2026-01-02T03:45:00Z"), // another instant
        Plain.Replace("}}", "},\"active\":true}", StringComparison.Ordinal), // another payload
        Plain.Replace("{\"resourceType\":\"Patient\",", "{ \"resourceType\":\"Patient\",", StringComparison.Ordinal), // the same JSON, written differently: bytes are the conservative witness
    };

    [Theory]
    [MemberData(nameof(ChangedReplays))]
    public async Task SecondRedemptionThatReturnsAnotherEntry_IsNotAReplay(string replayed)
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1")], [Searchsets.Entry(Plain)], [Searchsets.Entry(replayed)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReplayDiffered);
        result.IsObserved.ShouldBeFalse();
        shard.Server.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ReplayThatDiffersOnlyInTheNextLinkOrTotal_IsStillTheSameEntries()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            First(shard, Next(shard), Entry("a1")),
            ScriptedResponse.Json(Searchsets.Page(null, Next(shard, "B"), 10, Entry("b1", at: Late))),
            ScriptedResponse.Json(Searchsets.Page(null, null, 11, Entry("b1", at: Late))));

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.Observed);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public async Task ExchangeThatFails_IsAFailedRead_NotEvidence_AndLaterExchangesAreNotSent(int failingExchange, int expectedRequests)
    {
        await using var shard = ShardHarness.Start();
        ScriptedResponse[] script =
        [
            First(shard, Next(shard), Entry("a1")),
            Redeemed(Next(shard, "B"), Entry("b1", at: Late)),
            Redeemed(Next(shard, "C"), Entry("b1", at: Late)),
        ];
        script[failingExchange] = ScriptedResponse.Status(400, "synthetic refusal");
        shard.Server.Respond(script);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        result.IsObserved.ShouldBeFalse();
        shard.Server.Requests.Count.ShouldBe(expectedRequests);
    }

    [Fact]
    public async Task FirstPageThatDroppedTheSort_IsAFailedRead_BecauseItDoesNotAnswerTheSearchSent()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page($"{shard.Search}?_count=1", Next(shard), null, Entry("a1"))));

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NextLinkOfAnotherOrigin_IsRefusedByTheTransport_AndNothingIsRedeemed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(First(shard, $"https://127.0.0.1:1/fhir/Patient?{Sort}&ct=A", Entry("a1")));

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task NextLinkThatExceedsTheShardsRequestLimit_IsRefusedBeforeAnyRedemptionIsSent()
    {
        // The first target, "/fhir/Patient?_sort=_lastUpdated&_count=1", is exactly 41 bytes; its redemption is longer.
        await using var shard = ShardHarness.Start(limits: new ShardRequestLimits(maxGetTargetBytes: 41, maxPostBodyBytes: 1 << 20));
        Script(shard, [Entry("a1")], [Entry("b1", at: Late)]);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        shard.Server.Requests.Select(Wire).ShouldBe(["GET " + FirstTarget]);
    }

    [Fact]
    public async Task TransientFailure_IsRetriedOnlyByTheTransport_AndTheRetriedAnswerCanStillBeObserved()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            First(shard, Next(shard), Entry("a1")),
            ScriptedResponse.Status(503),
            Redeemed(Next(shard, "B"), Entry("b1", at: Late)),
            Redeemed(Next(shard, "C"), Entry("b1", at: Late)));

        (await ProbeAsync(shard)).Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.Observed);
        shard.Server.Requests.Select(Wire).ShouldBe(["GET " + FirstTarget, "GET " + RedeemedTarget, "GET " + RedeemedTarget, "GET " + RedeemedTarget]);
    }

    [Fact]
    public async Task PostOnAProfileWithoutPostSearch_IsNotOffered_AndNothingIsSent()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);

        LastUpdatedReplayProbeResult result = await ProbeAsync(shard, ShardSearchMethod.PostSearch);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.MethodNotOffered);
        result.StartMethod.ShouldBe(ShardSearchMethod.PostSearch);
        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GetOnAProfileWithoutPostSearch_IsObserved()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);
        Script(shard, [Entry("a1")], [Entry("b1", at: Late)]);

        (await ProbeAsync(shard)).IsObserved.ShouldBeTrue();
    }

    [Fact]
    public async Task Probe_WithADescriptorThatIsNotTheClientsOwn_IsRefusedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start();
        await using var other = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentException>(
            () => LastUpdatedReplayProbe.ProbeAsync(shard.Client, other.Shard, ShardHarness.Patient, ShardSearchMethod.Get, Ct));

        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task AnUndefinedMethod_IsRefusedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => ProbeAsync(shard, (ShardSearchMethod)99));

        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ACancelledToken_IsThrownBeforeAnythingIsSent()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => LastUpdatedReplayProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, ShardSearchMethod.PostSearch, cts.Token));

        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringARedemption_IsThrown_NotReportedAsAnOutcome()
    {
        await using var shard = ShardHarness.Start();
        var hold = new ResponseHold();
        shard.Server.Respond(
            First(shard, Next(shard), Entry("a1")),
            ScriptedResponse.Json(Searchsets.Page(null, null, null, Entry("b1", at: Late)), beforeHead: hold.BeforeHead));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<LastUpdatedReplayProbeResult> probe = LastUpdatedReplayProbe.ProbeAsync(
            shard.Client, shard.Shard, ShardHarness.Patient, ShardSearchMethod.Get, cts.Token);
        await hold.ArrivalAsync(Ct);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => probe);
    }

    [Fact]
    public async Task AFailureThatIsNotAShardCallFailure_PropagatesAndYieldsNoEvidence()
    {
        await using var shard = ShardHarness.Start();
        Script(shard, [Entry("a1")], [Entry("b1", at: Late)]);
        shard.Credentials.Acquire = _ => shard.Credentials.Calls == 2
            ? throw new InvalidOperationException("provider bug")
            : ValueTask.FromResult(SyntheticCredentials.Token);

        await Should.ThrowAsync<InvalidOperationException>(() => ProbeAsync(shard));
    }
}

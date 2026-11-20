using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Capabilities;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// <see cref="MultiShardHttpClient.ForRequest"/>: one request's transfer meter on the shared router, observed at real TLS
/// loopback servers. Every overlap is ordered by a <see cref="ResponseHold"/> that proves a request reached the wire, never
/// by a delay. The router's own meter stands for the host's background work.
/// </summary>
public sealed class MultiShardHttpClientRequestMeterTests
{
    private const string CallerHeader = "X-Federation-Caller";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Size(string body) => Encoding.UTF8.GetByteCount(body);

    [Fact]
    public async Task OverlappingRequestsOnOneShard_ChargeTheirOwnMetersAndADeniedSendReachesNoWire()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string page = MultiShardRig.Page(rig.East, null, Searchsets.Entry(MultiShardRig.PatientJson("a1", "east")));
        var hold = new ResponseHold();
        rig.East.Respond(ScriptedResponse.Json(page, hold.BeforeHead));
        var successful = new RecordingMeter();
        var denial = new InvalidOperationException("synthetic budget denied");
        var denied = new RecordingMeter { ThrowOnSend = denial };
        RequestShardClient requestA = rig.Router.ForRequest(successful);
        RequestShardClient requestB = rig.Router.ForRequest(denied);

        Task<ShardPage> running = requestA.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await hold.ArrivalAsync(Ct);
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => requestB.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct));
        rig.East.Requests.Count.ShouldBe(1);
        hold.Release();
        ShardPage result = await running.WaitAsync(Watchdog.Interval, Ct);

        thrown.ShouldBeSameAs(denial);
        result.Entries.Single().Key.ShouldBe(MultiShardRig.Key(MultiShardRig.EastId, "a1"));
        successful.Sends.ShouldBe([MultiShardRig.EastId]);
        successful.Bytes.ShouldBe(Size(page));
        denied.Sends.ShouldBe([MultiShardRig.EastId]);
        denied.Bytes.ShouldBe(0);
        rig.Meter.Sends.ShouldBeEmpty();
        rig.Meter.Bytes.ShouldBe(0);
        rig.East.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task BackgroundMetadataFetch_WhileARequestIsActive_ChargesOnlyTheRoutersOwnMeter()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string page = MultiShardRig.Page(rig.East);
        string statement = CapabilityJson.ServerStatement();
        var hold = new ResponseHold();
        rig.East.Respond(ScriptedResponse.Json(page, hold.BeforeHead), ScriptedResponse.Json(statement));
        var request = new RecordingMeter();
        Task<ShardPage> running = rig.Router.ForRequest(request).SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await hold.ArrivalAsync(Ct);

        await rig.Router.GetClient(rig.EastShard).GetCapabilitiesAsync(Ct);

        request.Sends.ShouldBe([MultiShardRig.EastId]);
        request.Bytes.ShouldBe(0);
        rig.Meter.Sends.ShouldBe([MultiShardRig.EastId]);
        rig.Meter.Bytes.ShouldBe(Size(statement));
        hold.Release();
        await running.WaitAsync(Watchdog.Interval, Ct);
        request.Sends.ShouldBe([MultiShardRig.EastId]);
        request.Bytes.ShouldBe(Size(page));
        rig.Meter.Bytes.ShouldBe(Size(statement));
        rig.East.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/metadata"]);
    }

    [Fact]
    public async Task RetryContinuationAndRead_KeepTheirMeterAndOpaqueCallerEvenWhenRequestsOverlap()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string first = MultiShardRig.Page(rig.East, rig.East.BaseUrl + "/Patient?ct=east-2", Searchsets.Entry(MultiShardRig.PatientJson("a1", "east")));
        string second = Searchsets.Linked();
        string readA = MultiShardRig.PatientJson("a2", "request A read");
        string readB = MultiShardRig.PatientJson("b1", "request B read, which is longer than the others");
        string metadata = CapabilityJson.ServerStatement();
        var hold = new ResponseHold();
        rig.East.Respond(
            ScriptedResponse.Status(503, beforeHead: hold.BeforeHead),
            ScriptedResponse.Json(readB),
            ScriptedResponse.Json(metadata),
            ScriptedResponse.Json(first),
            ScriptedResponse.Json(second),
            ScriptedResponse.Json(readA));
        var meterA = new RecordingMeter();
        var meterB = new RecordingMeter();
        const string callerA = "correlation_A-03";
        const string callerB = "correlation_B-19";
        RequestShardClient requestA = rig.Router.ForRequestWithCaller(meterA, callerA);
        using var ambient = new Activity("synthetic trace");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.AddBaggage(CallerHeader, "do-not-forward");
        ambient.Start();

        Task<ShardPage> firstPage = requestA.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await hold.ArrivalAsync(Ct);
        await rig.Router.ForRequestWithCaller(meterB, callerB).ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "b1"), Ct);
        await rig.Router.GetClient(rig.EastShard).GetCapabilitiesAsync(Ct);
        hold.Release();
        ShardPage page = await firstPage.WaitAsync(Watchdog.Interval, Ct);
        await requestA.ContinueAsync(page.Next!, Ct);
        await requestA.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "a2"), Ct);

        meterA.Sends.ShouldBe([MultiShardRig.EastId, MultiShardRig.EastId, MultiShardRig.EastId, MultiShardRig.EastId]);
        meterA.Bytes.ShouldBe(Size(first) + Size(second) + Size(readA));
        meterB.Sends.ShouldBe([MultiShardRig.EastId]);
        meterB.Bytes.ShouldBe(Size(readB));
        rig.Meter.Sends.ShouldBe([MultiShardRig.EastId]);
        rig.Meter.Bytes.ShouldBe(Size(metadata));
        rig.Credentials.Calls.ShouldBe(5);
        rig.East.Requests.Select(r => r.Target).ShouldBe(
            ["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient/b1", "/fhir/metadata", "/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient?ct=east-2", "/fhir/Patient/a2"]);
        rig.East.Requests.SelectMany(r => r.HeaderValues(CallerHeader).DefaultIfEmpty()).ShouldBe(
            [callerA, callerB, null, callerA, callerA, callerA]);
        foreach (RecordedRequest sent in rig.East.Requests)
        {
            sent.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
            sent.HeaderValues("traceparent").ShouldHaveSingleItem().ShouldContain(ambient.TraceId.ToHexString());
            sent.HeaderValues("baggage").ShouldBeEmpty();
            sent.HeaderValues("Correlation-Context").ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task PostAndLegacyView_DoNotCarryTheHeaderAcrossARedirectOrBetweenViews()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        rig.East.Respond(ScriptedResponse.Redirect(rig.West.BaseUrl + "/Patient"), ScriptedResponse.Json(MultiShardRig.Page(rig.East)));
        ShardSearchRequest get = MultiShardRig.Search(MultiShardRig.EastId);
        var post = new ShardSearchRequest(get.Shard, get.Type, get.Parameters, get.PageSize, ShardSearchMethod.PostSearch);
        string callerId = new('A', 128);

        ShardCallException refusal = await Should.ThrowAsync<ShardCallException>(() =>
            rig.Router.ForRequestWithCaller(new RecordingMeter(), callerId).SearchAsync(post, Ct));
        await rig.Router.ForRequest(new RecordingMeter()).SearchAsync(get, Ct);

        refusal.Failure.ShouldBe(ShardCallFailure.Redirect);
        rig.West.Requests.ShouldBeEmpty();
        rig.East.Requests.Select(r => r.Method).ShouldBe(["POST", "GET"]);
        rig.East.Requests[0].HeaderValues(CallerHeader).ShouldBe([callerId]);
        rig.East.Requests[1].HeaderValues(CallerHeader).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" a")]
    [InlineData("a,b")]
    [InlineData("a\r\nx-header: synthetic")]
    [InlineData("a\u00e9")]
    public async Task InvalidCallerIdCannotBindAView(string callerId)
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        var meter = new RecordingMeter();

        Should.Throw<ArgumentException>(() => rig.Router.ForRequestWithCaller(meter, callerId));
        Should.Throw<ArgumentException>(() => rig.Router.ForRequestWithCaller(meter, new string('A', 129)));
        Should.Throw<ArgumentNullException>(() => rig.Router.ForRequestWithCaller(meter, null!));

        meter.Sends.ShouldBeEmpty();
        rig.Credentials.Calls.ShouldBe(0);
        rig.East.Requests.ShouldBeEmpty();
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ParallelShardsOfOneRequest_ReportToItsMeterWhileAnotherRequestUsesTheSameShard()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string eastPage = MultiShardRig.Page(rig.East, null, Searchsets.Entry(MultiShardRig.PatientJson("e1", "east")));
        string westPage = MultiShardRig.Page(rig.West);
        string otherRead = MultiShardRig.PatientJson("w1", "another request's read of the west shard");
        var eastHold = new ResponseHold();
        var westHold = new ResponseHold();
        rig.East.Respond(ScriptedResponse.Json(eastPage, eastHold.BeforeHead));
        rig.West.Respond(ScriptedResponse.Json(westPage, westHold.BeforeHead), ScriptedResponse.Json(otherRead));
        var meterA = new RecordingMeter();
        var meterB = new RecordingMeter();
        RequestShardClient requestA = rig.Router.ForRequest(meterA);

        Task<ShardPage> east = requestA.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        Task<ShardPage> west = requestA.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct);
        await eastHold.ArrivalAsync(Ct);
        await westHold.ArrivalAsync(Ct);
        await rig.Router.ForRequest(meterB).ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "w1"), Ct);
        eastHold.Release();
        westHold.Release();
        await Task.WhenAll(east, west).WaitAsync(Watchdog.Interval, Ct);

        meterA.Sends.ShouldBe([MultiShardRig.EastId, MultiShardRig.WestId], ignoreOrder: true);
        meterA.Chunks.GroupBy(chunk => chunk.Shard)
            .Select(shard => (shard.Key, shard.Sum(chunk => chunk.Count)))
            .ShouldBe([(MultiShardRig.EastId, Size(eastPage)), (MultiShardRig.WestId, Size(westPage))], ignoreOrder: true);
        meterB.Sends.ShouldBe([MultiShardRig.WestId]);
        meterB.Chunks.ShouldAllBe(chunk => chunk.Shard == MultiShardRig.WestId);
        meterB.Bytes.ShouldBe(Size(otherRead));
        rig.Meter.Sends.ShouldBeEmpty();
        rig.Meter.Bytes.ShouldBe(0);
    }

    [Fact]
    public async Task RouterWithoutADefaultMeter_ChargesTheRequestMeterAndLeavesBackgroundCallsUncharged()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        using MultiShardHttpClient router = rig.CreateRouter(rig.Registry, meter: null);
        string page = MultiShardRig.Page(rig.East);
        var hold = new ResponseHold();
        rig.East.Respond(ScriptedResponse.Json(page, hold.BeforeHead), ScriptedResponse.Json(CapabilityJson.ServerStatement()));
        var request = new RecordingMeter();

        Task<ShardPage> running = router.ForRequest(request).SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await hold.ArrivalAsync(Ct);
        await router.GetClient(rig.EastShard).GetCapabilitiesAsync(Ct);

        request.Sends.ShouldBe([MultiShardRig.EastId]);
        request.Bytes.ShouldBe(0);
        hold.Release();
        await running.WaitAsync(Watchdog.Interval, Ct);
        request.Sends.ShouldBe([MultiShardRig.EastId]);
        request.Bytes.ShouldBe(Size(page));
        rig.East.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/metadata"]);
    }

    [Fact]
    public async Task RequestViewsAndTheRouter_ShareOneConnectionPool()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        rig.East.Respond(
            ScriptedResponse.Json(MultiShardRig.Page(rig.East)).KeepingAlive(),
            ScriptedResponse.Json(MultiShardRig.PatientJson("p1", "east")).KeepingAlive());

        await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await rig.Router.ForRequest(new RecordingMeter()).ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct);

        rig.East.Requests.Select(r => r.Connection).Distinct().ShouldBe([1]);
    }

    [Fact]
    public async Task RequestViewsAndTheRouter_ShareTheShardsCircuitBreaker()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        ShardSearchRequest post = new(MultiShardRig.EastId, ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 10, ShardSearchMethod.PostSearch);
        var tripping = new RecordingMeter();
        RequestShardClient failing = rig.Router.ForRequest(tripping);
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            rig.East.Respond(ScriptedResponse.Status(503));
            ShardCallException refused = await Should.ThrowAsync<ShardCallException>(() => failing.SearchAsync(post, Ct));
            refused.StatusCode.ShouldBe(503);
        }

        var later = new RecordingMeter();
        ShardCallException viaView = await Should.ThrowAsync<ShardCallException>(() => rig.Router.ForRequest(later).SearchAsync(post, Ct));
        ShardCallException viaRouter = await Should.ThrowAsync<ShardCallException>(() => rig.Router.SearchAsync(post, Ct));

        viaView.Failure.ShouldBe(ShardCallFailure.CircuitOpen);
        viaRouter.Failure.ShouldBe(ShardCallFailure.CircuitOpen);
        tripping.Sends.Count.ShouldBe(ShardResilience.MinimumThroughput);
        later.Sends.ShouldBeEmpty();
        rig.Meter.Sends.ShouldBeEmpty();
        rig.East.Requests.Count.ShouldBe(ShardResilience.MinimumThroughput);
    }

    [Fact]
    public async Task ForRequest_RefusesANullMeterAndAClosedRouter_AndAViewOutlivingItsRouterIsRefused()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        using MultiShardHttpClient router = rig.CreateRouter(rig.Registry);
        RequestShardClient view = router.ForRequest(new RecordingMeter());

        Should.Throw<ArgumentNullException>(() => router.ForRequest(null!));
        router.Dispose();

        Should.Throw<ObjectDisposedException>(() => router.ForRequest(new RecordingMeter()));
        Should.Throw<ObjectDisposedException>(() => { _ = view.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct); });
        Should.Throw<ObjectDisposedException>(() => { _ = view.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct); });
        rig.East.Requests.ShouldBeEmpty();
    }
}

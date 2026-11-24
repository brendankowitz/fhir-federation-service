using System.Collections;
using System.Text;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The per-shard request limits of the router: one explicit pair per registered shard, taken once, and bound to the client
/// that shard's own calls and lent client use (design spec §7.8). Observed at two TLS loopback servers.
/// </summary>
public sealed class MultiShardHttpClientLimitTests
{
    // "/fhir/Patient?name=Sarah&_count=10", 34 bytes on the wire.
    private const int SarahTargetBytes = 34;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public void Limits_RefuseNonPositiveCeilings(int get, int post)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardRequestLimits(get, post));
    }

    [Fact]
    public async Task EachShard_IsHeldToItsOwnLimits()
    {
        await using var rig = MultiShardRig.Start(
            eastLimits: new ShardRequestLimits(SarahTargetBytes, 1),
            westLimits: new ShardRequestLimits(SarahTargetBytes - 1, 1));
        rig.East.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.East)));

        await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct));

        failure.Shard.ShouldBe(MultiShardRig.WestId);
        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        rig.East.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient?name=Sarah&_count=10");
        rig.West.Requests.ShouldBeEmpty();
        rig.Meter.Sends.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OneShardsLimitsNeverApplyToTheOther()
    {
        await using var rig = MultiShardRig.Start(
            eastLimits: new ShardRequestLimits(1, 1),
            westLimits: SyntheticLimits.Generous);
        rig.West.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.West)));

        await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct);
        await Should.ThrowAsync<ShardCallException>(() => rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct));

        rig.West.Requests.ShouldHaveSingleItem();
        rig.East.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redemption_IsHeldToTheOwnersLimits()
    {
        await using var rig = MultiShardRig.Start(eastLimits: new ShardRequestLimits("/fhir/Patient?ct=Mg".Length - 1, 1));
        var cursor = new ShardCursor(MultiShardRig.EastId, ShardHarness.Patient, "?ct=Mg", NextLinkRedemption.FollowGet);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => rig.Router.ContinueAsync(cursor, Ct));

        failure.Failure.ShouldBe(ShardCallFailure.InvalidNextLink);
        rig.East.Requests.ShouldBeEmpty();
        rig.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task LentClient_CarriesTheSameLimitsAsTheRouter()
    {
        await using var rig = MultiShardRig.Start(westLimits: new ShardRequestLimits(SarahTargetBytes - 1, 1));
        HttpShardClient lent = rig.Router.GetClient(rig.WestShard);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => lent.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Limits_AreCopiedAtConstructionSoLaterChangesToTheCallersMapDoNothing()
    {
        await using var rig = MultiShardRig.Start();
        Dictionary<ShardId, ShardRequestLimits> map = Generous(rig);
        map[MultiShardRig.EastId] = new ShardRequestLimits(SarahTargetBytes, 1);
        using var router = Build(rig, map);
        map[MultiShardRig.EastId] = new ShardRequestLimits(1, 1);
        map.Remove(MultiShardRig.WestId);
        rig.East.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.East)));

        await router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);

        rig.East.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Construction_RefusesAMissingShard()
    {
        await using var rig = MultiShardRig.Start();
        Dictionary<ShardId, ShardRequestLimits> map = Generous(rig);
        map.Remove(MultiShardRig.WestId);

        Should.Throw<ArgumentException>(() => Build(rig, map));
    }

    [Fact]
    public async Task Construction_RefusesAnEntryForAnUnregisteredShard()
    {
        await using var rig = MultiShardRig.Start();
        Dictionary<ShardId, ShardRequestLimits> map = Generous(rig);
        map[MultiShardRig.GhostId] = SyntheticLimits.Generous;

        Should.Throw<ArgumentException>(() => Build(rig, map));
    }

    [Fact]
    public async Task Construction_RefusesANullEntry()
    {
        await using var rig = MultiShardRig.Start();
        Dictionary<ShardId, ShardRequestLimits> map = Generous(rig);
        map[MultiShardRig.WestId] = null!;

        Should.Throw<ArgumentException>(() => Build(rig, map));
    }

    [Fact]
    public async Task Construction_RefusesAShardListedTwice()
    {
        await using var rig = MultiShardRig.Start();
        var twice = new ListedMap(
            [
                new(MultiShardRig.EastId, SyntheticLimits.Generous),
                new(MultiShardRig.WestId, SyntheticLimits.Generous),
                new(MultiShardRig.EastId, new ShardRequestLimits(1, 1)),
            ]);

        Should.Throw<ArgumentException>(() => Build(rig, twice));
    }

    [Fact]
    public async Task Construction_DoesNotTrustTheCallersComparer()
    {
        await using var rig = MultiShardRig.Start();
        var everythingEqual = new Dictionary<ShardId, ShardRequestLimits>(new AllEqual())
        {
            [MultiShardRig.EastId] = SyntheticLimits.Generous,
        };

        // One entry cannot stand for two registered shards, however the caller's comparer folds their ids.
        Should.Throw<ArgumentException>(() => Build(rig, everythingEqual));
    }

    [Fact]
    public async Task Construction_RefusesMissingLimits()
    {
        await using var rig = MultiShardRig.Start();

        Should.Throw<ArgumentNullException>(() => Build(rig, null!));
    }

    // Preflight against the actual wire. Each case states, as independent literals, the query a backend receives and the
    // bytes it would count: the GET target ("/fhir/Patient?" is 14 bytes) and the POST form body.
    [Theory]
    [InlineData(ShardSearchMethod.Get, "plain")]
    [InlineData(ShardSearchMethod.PostSearch, "plain")]
    [InlineData(ShardSearchMethod.Get, "raw-repeated")]
    [InlineData(ShardSearchMethod.PostSearch, "raw-repeated")]
    [InlineData(ShardSearchMethod.Get, "summary-count")]
    [InlineData(ShardSearchMethod.PostSearch, "summary-count")]
    public async Task FitsSearchRequest_ExactBoundFitsAndOneByteShortDoesNot_AsTheWireConfirms(ShardSearchMethod method, string name)
    {
        WireCase wire = WireCase.Named(name);
        int bytes = method == ShardSearchMethod.Get ? wire.GetTargetBytes : wire.PostBodyBytes;
        // The other method's ceiling is one byte, so a preflight of the wrong method could not fit.
        await using var rig = MultiShardRig.Start(eastLimits: Only(method, bytes), westLimits: Only(method, bytes - 1));
        ShardSearchRequest east = wire.Request(MultiShardRig.EastId, method);
        ShardSearchRequest west = wire.Request(MultiShardRig.WestId, method);

        bool eastFits = rig.Router.FitsSearchRequest(east);
        bool westFits = rig.Router.FitsSearchRequest(west);

        eastFits.ShouldBeTrue();
        westFits.ShouldBeFalse();
        AssertNothingHappened(rig);

        rig.East.Respond(ScriptedResponse.Json(wire.Page(rig.East)));
        await rig.Router.SearchAsync(east, Ct);
        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => rig.Router.SearchAsync(west, Ct));

        RecordedRequest sent = rig.East.Requests.ShouldHaveSingleItem();
        if (method == ShardSearchMethod.Get)
        {
            (sent.Method, sent.Target).ShouldBe(("GET", "/fhir/Patient?" + wire.Query));
            sent.Body.ShouldBeEmpty();
            Encoding.ASCII.GetByteCount(sent.Target).ShouldBe(bytes);
        }
        else
        {
            (sent.Method, sent.Target).ShouldBe(("POST", "/fhir/Patient/_search"));
            Encoding.ASCII.GetString(sent.Body).ShouldBe(wire.Query);
            sent.Body.Length.ShouldBe(bytes);
        }

        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        rig.West.Requests.ShouldBeEmpty();
        rig.Meter.Sends.ShouldHaveSingleItem();
        rig.Credentials.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get)]
    [InlineData(ShardSearchMethod.PostSearch)]
    public async Task FitsSearchRequest_DoesNotFitItsOwnMethod_EvenWhenTheOtherMethodWould(ShardSearchMethod method)
    {
        WireCase wire = WireCase.Named("plain");
        // The unselected method's ceiling is generous, so a preflight that chose a verb for the caller would say it fits.
        ShardRequestLimits limits = method == ShardSearchMethod.Get
            ? new(maxGetTargetBytes: wire.GetTargetBytes - 1, maxPostBodyBytes: 1 << 20)
            : new(maxGetTargetBytes: 1 << 20, maxPostBodyBytes: wire.PostBodyBytes - 1);
        await using var rig = MultiShardRig.Start(eastLimits: limits);

        rig.Router.FitsSearchRequest(wire.Request(MultiShardRig.EastId, method)).ShouldBeFalse();
        AssertNothingHappened(rig);
    }

    [Fact]
    public async Task FitsSearchRequest_RefusesWhatSendRefuses_WithoutSideEffects()
    {
        await using var rig = MultiShardRig.Start();

        Should.Throw<ArgumentNullException>(() => rig.Router.FitsSearchRequest(null!));
        Should.Throw<ArgumentException>(() => rig.Router.FitsSearchRequest(MultiShardRig.Search(MultiShardRig.GhostId)));
        Should.Throw<ArgumentException>(() => rig.Router.FitsSearchRequest(
            new ShardSearchRequest(MultiShardRig.EastId, ShardHarness.Patient, [new ParameterOccurrence("name", "a#b")], 10, ShardSearchMethod.Get)));
        Should.Throw<ArgumentException>(() => rig.Router.FitsSearchRequest(
            new ShardSearchRequest(MultiShardRig.EastId, ShardHarness.Patient, [new ParameterOccurrence("name", "100%")], 10, ShardSearchMethod.PostSearch)));
        Should.Throw<ArgumentOutOfRangeException>(() => rig.Router.FitsSearchRequest(
            new ShardSearchRequest(MultiShardRig.EastId, ShardHarness.Patient, [], 51, ShardSearchMethod.Get)));
        AssertNothingHappened(rig);

        rig.Router.Dispose();
        Should.Throw<ObjectDisposedException>(() => rig.Router.FitsSearchRequest(MultiShardRig.Search(MultiShardRig.EastId)));
    }

    [Fact]
    public async Task FitsSearchRequest_RefusesPostWhereTheBackendHasNone()
    {
        await using var shard = ShardHarness.Start(profile: ShardHarness.GetOnly);

        Should.Throw<ArgumentException>(() => shard.Client.FitsSearch(shard.SarahRequest(ShardSearchMethod.PostSearch)));
        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FitsSearchRequest_CountsTheCanonicalBasePathOfAGetTarget()
    {
        // "/tenant%2Fa/fhir/Patient?name=Sarah&_count=10" is 45 bytes; the base path is canonicalized as it is sent.
        const string target = "/tenant%2Fa/fhir/Patient?name=Sarah&_count=10";
        await using var exact = ShardHarness.Start(basePath: "/tenant%2fa/fhir", limits: new(maxGetTargetBytes: target.Length, maxPostBodyBytes: 1));
        await using var under = ShardHarness.Start(basePath: "/tenant%2fa/fhir", limits: new(maxGetTargetBytes: target.Length - 1, maxPostBodyBytes: 1));
        exact.Server.Respond(ScriptedResponse.Json(exact.SarahPage()));

        exact.Client.FitsSearch(exact.SarahRequest()).ShouldBeTrue();
        under.Client.FitsSearch(under.SarahRequest()).ShouldBeFalse();
        exact.Server.Requests.ShouldBeEmpty();

        await exact.Client.SearchAsync(exact.SarahRequest(), Ct);
        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => under.Client.SearchAsync(under.SarahRequest(), Ct));

        exact.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe(target);
        failure.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        under.Server.Requests.ShouldBeEmpty();
    }

    private static ShardRequestLimits Only(ShardSearchMethod method, int bytes) =>
        method == ShardSearchMethod.Get ? new(maxGetTargetBytes: bytes, maxPostBodyBytes: 1) : new(maxGetTargetBytes: 1, maxPostBodyBytes: bytes);

    private static void AssertNothingHappened(MultiShardRig rig)
    {
        rig.East.Requests.ShouldBeEmpty();
        rig.West.Requests.ShouldBeEmpty();
        rig.Credentials.Calls.ShouldBe(0);
        rig.Meter.Sends.ShouldBeEmpty();
    }

    private sealed record WireCase(ParameterOccurrence[] Parameters, int PageSize, string Query, int GetTargetBytes, int PostBodyBytes)
    {
        public static WireCase Named(string name) => name switch
        {
            "plain" => new([new("name", "Sarah")], 10, "name=Sarah&_count=10", 34, 20),

            // Escapes and a repeated occurrence stay as written, in order, so the wire is longer than the decoded text.
            "raw-repeated" => new(
                [new("name", "%41%2fb"), new("name", "%41%2fb"), new("code", "a%7Cb")],
                10,
                "name=%41%2fb&name=%41%2fb&code=a%7Cb&_count=10",
                60,
                46),

            // A supplied _summary=count is kept, and the appended _count=0 follows it.
            "summary-count" => new([new("_summary", "count")], 0, "_summary=count&_count=0", 37, 23),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        public ShardSearchRequest Request(ShardId shard, ShardSearchMethod method) =>
            new(shard, ShardHarness.Patient, Parameters, PageSize, method);

        // Passes the self check for the query sent; a count-only page carries a total and nothing else.
        public string Page(LoopbackFhirServer server) =>
            Searchsets.Page(server.BaseUrl + "/Patient?" + Query, next: null, total: PageSize == 0 ? 0 : null);
    }

    private static Dictionary<ShardId, ShardRequestLimits> Generous(MultiShardRig rig) =>
        new() { [MultiShardRig.EastId] = SyntheticLimits.Generous, [MultiShardRig.WestId] = SyntheticLimits.Generous };

    private static MultiShardHttpClient Build(MultiShardRig rig, IReadOnlyDictionary<ShardId, ShardRequestLimits> limits) =>
        new(rig.Registry, rig.Credentials, TimeSpan.FromSeconds(10), 1 << 20, limits, rig.Meter, LoopbackCertificate.Trust);

    private sealed class AllEqual : IEqualityComparer<ShardId>
    {
        public bool Equals(ShardId? x, ShardId? y) => true;

        public int GetHashCode(ShardId obj) => 0;
    }

    // A map that can list one key twice, which no dictionary can hold.
    private sealed class ListedMap(List<KeyValuePair<ShardId, ShardRequestLimits>> entries) : IReadOnlyDictionary<ShardId, ShardRequestLimits>
    {
        public int Count => entries.Count;

        public IEnumerable<ShardId> Keys => entries.Select(e => e.Key);

        public IEnumerable<ShardRequestLimits> Values => entries.Select(e => e.Value);

        public ShardRequestLimits this[ShardId key] => entries.First(e => e.Key == key).Value;

        public bool ContainsKey(ShardId key) => entries.Any(e => e.Key == key);

        public bool TryGetValue(ShardId key, out ShardRequestLimits value)
        {
            value = entries.FirstOrDefault(e => e.Key == key).Value!;
            return value is not null;
        }

        public IEnumerator<KeyValuePair<ShardId, ShardRequestLimits>> GetEnumerator() => entries.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

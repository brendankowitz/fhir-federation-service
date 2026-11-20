using System.Collections;
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

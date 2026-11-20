using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Capabilities;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The registry-backed router, observed at two real TLS loopback servers (design spec §4, §5, §7, §8, §12): each call goes
/// to the shard its own request, cursor or key names, through one long-lived client per shard.
/// </summary>
public sealed class MultiShardHttpClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SameQualifiedTypeAndId_IsSearchedAndReadFromItsOwnShard()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string eastEntry = Searchsets.Entry(MultiShardRig.PatientJson("dup", "east"));
        string westEntry = Searchsets.Entry(MultiShardRig.PatientJson("dup", "west"));
        byte[] eastBody = Encoding.UTF8.GetBytes(MultiShardRig.PatientJson("dup", "east"));
        byte[] westBody = Encoding.UTF8.GetBytes(MultiShardRig.PatientJson("dup", "west"));
        rig.East.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.East, null, eastEntry)), ScriptedResponse.Bytes(eastBody));
        rig.West.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.West, null, westEntry)), ScriptedResponse.Bytes(westBody));

        ShardPage eastPage = await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        ShardPage westPage = await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct);
        ShardReadResult eastRead = await rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "dup"), Ct);
        ShardReadResult westRead = await rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "dup"), Ct);

        eastPage.Entries.Single().Key.ShouldBe(MultiShardRig.Key(MultiShardRig.EastId, "dup"));
        westPage.Entries.Single().Key.ShouldBe(MultiShardRig.Key(MultiShardRig.WestId, "dup"));
        eastRead.Resource.ToArray().ShouldBe(eastBody);
        westRead.Resource.ToArray().ShouldBe(westBody);
        eastRead.ContentLocation.OriginalString.ShouldBe(rig.East.BaseUrl + "/Patient/dup");
        westRead.ContentLocation.OriginalString.ShouldBe(rig.West.BaseUrl + "/Patient/dup");
        rig.East.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient/dup"]);
        rig.West.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient/dup"]);
        rig.Meter.Sends.ShouldBe([MultiShardRig.EastId, MultiShardRig.WestId, MultiShardRig.EastId, MultiShardRig.WestId]);
    }

    [Fact]
    public async Task Cursor_IsRedeemedAtTheShardItNames()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        rig.East.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.East, rig.East.BaseUrl + "/Patient?ct=east-2")));
        rig.West.Respond(ScriptedResponse.Json(MultiShardRig.Page(rig.West, rig.West.BaseUrl + "/Patient?ct=west-2")));
        ShardPage eastFirst = await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        ShardPage westFirst = await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct);
        rig.East.Respond(ScriptedResponse.Json(Searchsets.Linked()));
        rig.West.Respond(ScriptedResponse.Json(Searchsets.Linked()));

        await rig.Router.ContinueAsync(westFirst.Next!, Ct);
        await rig.Router.ContinueAsync(eastFirst.Next!, Ct);

        rig.East.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient?ct=east-2"]);
        rig.West.Requests.Select(r => r.Target).ShouldBe(["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient?ct=west-2"]);
    }

    [Fact]
    public async Task CursorWhoseTargetBelongsToAnotherShard_IsRefusedByItsOwnerAndNeverRerouted()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        var foreign = new ShardCursor(MultiShardRig.EastId, ShardHarness.Patient, rig.West.BaseUrl + "/Patient?ct=Mg", NextLinkRedemption.FollowGet);

        Should.Throw<ArgumentException>(() => { _ = rig.Router.ContinueAsync(foreign, Ct); });

        rig.Credentials.Calls.ShouldBe(0);
        rig.Meter.Sends.ShouldBeEmpty();
        rig.East.Requests.ShouldBeEmpty();
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnknownOwner_IsRefusedForEveryOperationBeforeAnyCredentialOrSend()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        var cursor = new ShardCursor(MultiShardRig.GhostId, ShardHarness.Patient, rig.East.BaseUrl + "/Patient?ct=Mg", NextLinkRedemption.FollowGet);

        ArgumentException search = Should.Throw<ArgumentException>(() => { _ = rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.GhostId), Ct); });
        ArgumentException next = Should.Throw<ArgumentException>(() => { _ = rig.Router.ContinueAsync(cursor, Ct); });
        ArgumentException read = Should.Throw<ArgumentException>(() => { _ = rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.GhostId, "p1"), Ct); });

        foreach (ArgumentException refusal in new[] { search, next, read })
        {
            refusal.Message.ShouldNotContain("ghost");
            refusal.InnerException.ShouldBeNull();
        }

        rig.Credentials.Calls.ShouldBe(0);
        rig.Meter.Sends.ShouldBeEmpty();
        rig.East.Requests.ShouldBeEmpty();
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NullArguments_AreRefused()
    {
        await using MultiShardRig rig = MultiShardRig.Start();

        Should.Throw<ArgumentNullException>(() => { _ = rig.Router.SearchAsync(null!, Ct); });
        Should.Throw<ArgumentNullException>(() => { _ = rig.Router.ContinueAsync(null!, Ct); });
        Should.Throw<ArgumentNullException>(() => { _ = rig.Router.ReadAsync(null!, Ct); });
        Should.Throw<ArgumentNullException>(() => rig.Router.GetClient(null!));
    }

    [Fact]
    public async Task GetClient_LendsTheRegisteredShardsOwnClientEveryTime()
    {
        await using MultiShardRig rig = MultiShardRig.Start();

        HttpShardClient east = rig.Router.GetClient(rig.EastShard);
        HttpShardClient west = rig.Router.GetClient(rig.WestShard);

        rig.Router.GetClient(rig.EastShard).ShouldBeSameAs(east);
        west.ShouldNotBeSameAs(east);
    }

    [Fact]
    public async Task GetClient_RefusesADescriptorThatIsNotTheRegistrysOwn()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        ShardDescriptor sameIdOtherSnapshot = rig.CreateRegistry().Shards[0];
        sameIdOtherSnapshot.Id.ShouldBe(rig.EastShard.Id);

        Should.Throw<ArgumentException>(() => rig.Router.GetClient(sameIdOtherSnapshot));
    }

    [Fact]
    public async Task RouterAndBorrowedMetadataClient_ReuseTheSameConnection()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        string body = MultiShardRig.PatientJson("p1", "east");
        rig.East.Respond(
            ScriptedResponse.Json(MultiShardRig.Page(rig.East)).KeepingAlive(),
            ScriptedResponse.Json(body).KeepingAlive(),
            ScriptedResponse.Json(CapabilityJson.ServerStatement()).KeepingAlive());
        HttpShardClient borrowed = rig.Router.GetClient(rig.EastShard);

        await rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct);
        await rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct);
        await borrowed.GetCapabilitiesAsync(Ct);

        rig.East.Requests.Select(r => r.Target).ShouldBe(
            ["/fhir/Patient?name=Sarah&_count=10", "/fhir/Patient/p1", "/fhir/metadata"]);
        rig.East.Requests.Select(r => r.Connection).Distinct().ShouldBe([1]);
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task EachShard_FailsInItsOwnTerms()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        rig.East.Respond(ScriptedResponse.Status(404, "secret internal detail"));
        rig.West.Respond(ScriptedResponse.Json(MultiShardRig.PatientJson("p1", "west")));

        ShardCallException missing = await Should.ThrowAsync<ShardCallException>(() => rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct));
        ShardReadResult found = await rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "p1"), Ct);

        missing.Failure.ShouldBe(ShardCallFailure.ResourceNotFound);
        missing.Shard.ShouldBe(MultiShardRig.EastId);
        found.ContentLocation.OriginalString.ShouldBe(rig.West.BaseUrl + "/Patient/p1");
    }

    [Fact]
    public async Task MeterFailure_PropagatesUnchangedFromTheSharedMeter()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        var budget = new InvalidOperationException("synthetic budget exhausted");
        rig.Meter.ThrowOnSend = budget;

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.WestId), Ct));

        thrown.ShouldBeSameAs(budget);
        rig.Meter.Sends.ShouldBe([MultiShardRig.WestId]);
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TimeoutAndResponseSizeLimit_ApplyToEveryShardCall()
    {
        await using MultiShardRig slow = MultiShardRig.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        slow.West.Respond(ScriptedResponse.Stall());
        await using MultiShardRig small = MultiShardRig.Start(maxResponseBytes: 64);
        small.West.Respond(ScriptedResponse.Json(MultiShardRig.PatientJson("p1", new string('x', 200))));

        ShardCallException timeout = await Should.ThrowAsync<ShardCallException>(() => slow.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "p1"), Ct));
        ShardCallException tooLarge = await Should.ThrowAsync<ShardCallException>(() => small.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "p1"), Ct));

        timeout.Failure.ShouldBe(ShardCallFailure.Timeout);
        tooLarge.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
    }

    [Fact]
    public async Task CancelledCall_SendsNothingAndPropagatesCancellation()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => rig.Router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), caller.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => rig.Router.ReadAsync(MultiShardRig.Key(MultiShardRig.WestId, "p1"), caller.Token));

        rig.Credentials.Calls.ShouldBe(0);
        rig.East.Requests.ShouldBeEmpty();
        rig.West.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispose_ClosesTheClientsAndRefusesLaterAccessButLeavesTheCallersCollaborators()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        using var credentials = new DisposalTrackingCredentials();
        using var meter = new DisposalTrackingMeter();
        var router = new MultiShardHttpClient(rig.Registry, credentials, TimeSpan.FromSeconds(10), 1 << 20, meter, LoopbackCertificate.Trust);
        HttpShardClient borrowed = router.GetClient(rig.EastShard);
        var cursor = new ShardCursor(MultiShardRig.EastId, ShardHarness.Patient, rig.East.BaseUrl + "/Patient?ct=Mg", NextLinkRedemption.FollowGet);

        router.Dispose();
        router.Dispose();

        Should.Throw<ObjectDisposedException>(() => { _ = router.SearchAsync(MultiShardRig.Search(MultiShardRig.EastId), Ct); });
        Should.Throw<ObjectDisposedException>(() => { _ = router.ContinueAsync(cursor, Ct); });
        Should.Throw<ObjectDisposedException>(() => { _ = router.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct); });
        Should.Throw<ObjectDisposedException>(() => router.GetClient(rig.EastShard));
        await Should.ThrowAsync<ObjectDisposedException>(() => borrowed.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct));
        credentials.Disposed.ShouldBeFalse();
        meter.Disposed.ShouldBeFalse();
        rig.East.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedConstruction_DisposesTheClientsAlreadyBuiltAndKeepsTheOriginalError()
    {
        await using MultiShardRig rig = MultiShardRig.Start();
        var built = new List<HttpShardClient>();
        var failure = new InvalidOperationException("synthetic construction failure");

        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(() => new MultiShardHttpClient(
            rig.Registry,
            descriptor =>
            {
                if (descriptor.Id == MultiShardRig.WestId)
                {
                    throw failure;
                }

                var client = new HttpShardClient(descriptor, rig.Credentials, TimeSpan.FromSeconds(10), 1 << 20, rig.Meter, LoopbackCertificate.Trust);
                built.Add(client);
                return client;
            }));

        thrown.ShouldBeSameAs(failure);
        HttpShardClient eastClient = built.ShouldHaveSingleItem();
        await Should.ThrowAsync<ObjectDisposedException>(() => eastClient.ReadAsync(MultiShardRig.Key(MultiShardRig.EastId, "p1"), Ct));
    }

    [Fact]
    public async Task PublicConstructor_RefusesBadArgumentsWithTheClientsOwnValidation()
    {
        await using MultiShardRig rig = MultiShardRig.Start();

        Should.Throw<ArgumentNullException>(() => new MultiShardHttpClient(null!, rig.Credentials, TimeSpan.FromSeconds(1), 1024));
        Should.Throw<ArgumentNullException>(() => new MultiShardHttpClient(rig.Registry, null!, TimeSpan.FromSeconds(1), 1024));
        Should.Throw<ArgumentOutOfRangeException>(() => new MultiShardHttpClient(rig.Registry, rig.Credentials, TimeSpan.Zero, 1024));
        Should.Throw<ArgumentOutOfRangeException>(() => new MultiShardHttpClient(rig.Registry, rig.Credentials, TimeSpan.FromSeconds(1), 0));
    }

    private sealed class DisposalTrackingCredentials : IShardCredentialProvider, IDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask<string> GetAccessTokenAsync(ShardAuth auth, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SyntheticCredentials.Token);

        public void Dispose() => Disposed = true;
    }

    private sealed class DisposalTrackingMeter : IShardTransferMeter, IDisposable
    {
        public bool Disposed { get; private set; }

        public void RecordSend(ShardId shard)
        {
        }

        public void RecordResponseBytes(ShardId shard, int count)
        {
        }

        public void Dispose() => Disposed = true;
    }
}

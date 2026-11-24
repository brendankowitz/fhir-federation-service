using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.UnitTests.Http;
using Ignixa.Federation.Shards.UnitTests.Probes;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// Admission as a caller sees it (design spec §§6.2, 9.2): over a real TLS loopback transport and the production
/// metadata decoding and probes, what is sent, and when a proof for the refresher's current advertisement is returned.
/// </summary>
public sealed class ShardCapabilityAdmissionTests
{
    private const string Patient = "Patient";

    private const NextLinkRedemption Repost = NextLinkRedemption.RepostToSearch;
    private const NextLinkRedemption Follow = NextLinkRedemption.FollowGet;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] SuccessfulWires(ShardSearchMethod method, NextLinkRedemption redemption) =>
    [
        ProbeRoutes.ControlWire(Patient, method),
        ProbeRoutes.ChallengeWire(Patient, method),
        ProbeRoutes.StartWire(Patient, method),
        ProbeRoutes.RedeemWire(Patient, method, redemption),
        ProbeRoutes.RedeemWire(Patient, method, redemption),
    ];

    [Fact]
    public async Task GetOnlyShard_WithMatchingIdentityAndPassingProbes_YieldsAProofForTheRefreshersExactEvidence()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityEvidence evidence = rig.Evidence.ShouldNotBeNull();

        ShardCapabilityProof? proof = await rig.ProveAsync(Ct);

        proof.ShouldNotBeNull();
        proof.Shard.ShouldBeSameAs(rig.Shard.Shard);
        proof.Evidence.ShouldBeSameAs(evidence);
        proof.Certification.ShouldBeSameAs(rig.Certification);
        rig.Wires.ShouldBe([ProbeRoutes.Metadata, .. SuccessfulWires(ShardSearchMethod.Get, Follow)]);
        foreach (RecordedRequest sent in rig.Shard.Server.Requests.Skip(1))
        {
            sent.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
        }

        rig.Shard.Meter.Sends.Count.ShouldBe(6, "the refresher's fetch plus five probe exchanges, none a second metadata fetch");
        rig.Refresher.Current.TryCreateSearchCapabilities([proof], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ShardOfferingPost_IsProvenForGetThenPost_OnTheSamePinnedEvidence()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.Repost);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Repost);
        rig.Routes.Succeed(Patient, ShardSearchMethod.PostSearch, Repost);

        ShardCapabilityProof? proof = await rig.ProveAsync(Ct);

        proof.ShouldNotBeNull();
        proof.Evidence.ShouldBeSameAs(rig.Evidence);
        rig.Wires.ShouldBe(
        [
            ProbeRoutes.Metadata,
            .. SuccessfulWires(ShardSearchMethod.Get, Repost),
            .. SuccessfulWires(ShardSearchMethod.PostSearch, Repost),
        ]);
    }

    [Fact]
    public async Task PostFailingAfterAValidGet_IsRefused_AndSendsNothingMore()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.Repost);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Repost);
        rig.Routes.Strict(Patient, ShardSearchMethod.PostSearch, challenge: ProbeScript.EchoedChallenge(rig.Shard));

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBe(
        [
            ProbeRoutes.Metadata,
            .. SuccessfulWires(ShardSearchMethod.Get, Repost),
            ProbeRoutes.ControlWire(Patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.PostSearch),
        ]);
    }

    [Fact]
    public async Task StrictChallengeAnsweredOtherThan400_IsRefused_BeforeAnyReplayIsSent()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Strict(Patient, ShardSearchMethod.Get, challenge: ProbeScript.EchoedChallenge(rig.Shard));

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBe(
            [ProbeRoutes.Metadata, ProbeRoutes.ControlWire(Patient, ShardSearchMethod.Get), ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.Get)]);
    }

    [Fact]
    public async Task InconclusiveReplay_IsRefused()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Strict(Patient, ShardSearchMethod.Get);
        rig.Routes.Replay(Patient, ShardSearchMethod.Get, Follow, first: rig.Routes.FirstPage(Patient, ShardSearchMethod.Get, withNext: false));

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.Last().ShouldBe(ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get), "a first page without a cursor ends the sample");
    }

    [Fact]
    public async Task CredentialOutageDuringAProbe_IsRefused_ButAProviderBugPropagates()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        rig.Shard.Credentials.Acquire = _ => throw new ShardCredentialException(ShardCredentialFailure.TokenUnavailable);

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Shard.Credentials.Acquire = _ => throw new InvalidOperationException("synthetic provider bug");
        await Should.ThrowAsync<InvalidOperationException>(() => rig.ProveAsync(Ct));
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData(null)]
    public async Task UnmatchedOrMissingIdentity_IsRefused_BeforeAnyProbe(string? version)
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly, AdmissionRig.Advertise(version: version));

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task UnadvertisedProbeType_IsRefused_BeforeAnyProbe()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);

        (await rig.ProveAsync(Ct, ResourceType.Parse("Observation"))).ShouldBeNull();

        rig.Wires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task NoEvidenceYet_IsRefused_BeforeAnyProbe()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly, ScriptedResponse.Status(503));
        rig.Evidence.ShouldBeNull();

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task EvidenceTheStandaloneParserProduced_IsRefused_EvenWhenItReadsAsTheAdvertisement()
    {
        ShardCapabilityEvidence parsed = CapabilityJson.Read(AdmissionRig.Statement());
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly, fetch: _ => AdmissionRig.Returning(parsed));
        rig.Evidence.ShouldBeSameAs(parsed);

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBeEmpty();
    }

    [Fact]
    public async Task EvidenceTransportedFromAnotherDescriptor_IsRefused()
    {
        await using AdmissionRig other = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        ShardCapabilityEvidence foreign = other.Evidence.ShouldNotBeNull();
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly, fetch: _ => AdmissionRig.Returning(foreign));

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescriptorOutsideTheRefreshersRegistry_IsRefused_EvenWithItsOwnTransportedEvidence()
    {
        await using AdmissionRig other = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        ShardCapabilityEvidence foreign = other.Evidence.ShouldNotBeNull();
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly, fetch: _ => AdmissionRig.Returning(foreign));
        other.Shard.Shard.Id.ShouldBe(rig.Shard.Shard.Id);
        other.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);

        ShardCapabilityProof? proof = await ShardCapabilityAdmission.TryProveAsync(
            other.Shard.Client, other.Shard.Shard, rig.Refresher, AdmissionRig.Patient, AdmissionRig.Policy, rig.Certification, Ct);

        proof.ShouldBeNull();
        other.Wires.ShouldBe([ProbeRoutes.Metadata]);
        rig.Wires.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClientBuiltForAnotherShard_IsAnArgumentError_AndSendsNothing()
    {
        await using AdmissionRig other = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);

        await Should.ThrowAsync<ArgumentException>(() => ShardCapabilityAdmission.TryProveAsync(
            other.Shard.Client, rig.Shard.Shard, rig.Refresher, AdmissionRig.Patient, AdmissionRig.Policy, rig.Certification, Ct));

        rig.Wires.ShouldBe([ProbeRoutes.Metadata]);
        other.Wires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task ACancelledToken_IsThrown_BeforeAnythingIsSent()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => rig.ProveAsync(cts.Token));

        rig.Wires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task CancellationDuringAProbe_IsThrown_NotReportedAsARefusal()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        var hold = new ResponseHold();
        rig.Routes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<ShardCapabilityProof?> admission = rig.ProveAsync(cts.Token);
        await hold.ArrivalAsync(Ct);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => admission);
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheExactEvidence_AndAdmissionProvesIt()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityProof earlier = (await rig.ProveAsync(Ct)).ShouldNotBeNull();
        rig.Routes.Advertisement = ScriptedResponse.Status(503);
        await rig.RefreshAsync();
        rig.Refresher.Current.Shards[rig.Shard.Shard.Id].IsDegraded.ShouldBeTrue();
        rig.Refresher.Current.TryCreateSearchCapabilities([earlier], out _).ShouldBeTrue();
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);

        ShardCapabilityProof? proof = await rig.ProveAsync(Ct);

        proof.ShouldNotBeNull();
        proof.Evidence.ShouldBeSameAs(earlier.Evidence);
    }

    [Fact]
    public async Task SuccessfulIdenticalRefresh_NeedsItsOwnProof_AndTheEarlierProofNoLongerBinds()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityProof earlier = (await rig.ProveAsync(Ct)).ShouldNotBeNull();

        await rig.RefreshAsync();
        rig.Evidence.ShouldNotBeSameAs(earlier.Evidence);
        rig.Refresher.Current.TryCreateSearchCapabilities([earlier], out _).ShouldBeFalse();
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityProof? replacement = await rig.ProveAsync(Ct);

        replacement.ShouldNotBeNull();
        replacement.Evidence.ShouldBeSameAs(rig.Evidence);
        rig.Refresher.Current.TryCreateSearchCapabilities([replacement], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ContradictoryRefresh_IsJudgedByTheNewAdvertisement_AndSendsNoProbe()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityProof earlier = (await rig.ProveAsync(Ct)).ShouldNotBeNull();
        rig.Routes.Advertisement = AdmissionRig.Advertise(version: "2.0.0");
        await rig.RefreshAsync();
        rig.Refresher.Current.TryCreateSearchCapabilities([earlier], out _).ShouldBeFalse();

        (await rig.ProveAsync(Ct)).ShouldBeNull();

        rig.Wires.ShouldBe([ProbeRoutes.Metadata, .. SuccessfulWires(ShardSearchMethod.Get, Follow), ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task RefreshPublishedWhileProbing_RefusesTheStaleEvidence_ThenAProofForTheReplacementIsPossible()
    {
        await using AdmissionRig rig = await AdmissionRig.StartAsync(ShardHarness.GetOnly);
        var hold = new ResponseHold();
        rig.Routes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        rig.Routes.Replay(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityEvidence stale = rig.Evidence.ShouldNotBeNull();

        Task<ShardCapabilityProof?> admission = rig.ProveAsync(Ct);
        await hold.ArrivalAsync(Ct);
        await rig.RefreshAsync(); // the same content, a new instance
        rig.Evidence.ShouldNotBeSameAs(stale);
        hold.Release();

        (await admission).ShouldBeNull("every probe passed, but only against evidence that is no longer current");
        rig.Routes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        ShardCapabilityProof? replacement = await rig.ProveAsync(Ct);
        replacement.ShouldNotBeNull();
        replacement.Evidence.ShouldBeSameAs(rig.Evidence);
    }
}

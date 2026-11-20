using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// The strict-handling probe as a caller sees it: what it sends over a real TLS loopback transport, and which answers it
/// accepts as proof that a shard rejects an unadvertised parameter (design spec §6.2, §9.2).
/// </summary>
public sealed class StrictHandlingProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ShardSearchMethod> Methods => new() { ShardSearchMethod.Get, ShardSearchMethod.PostSearch };

    private static Task<StrictHandlingProbeResult> ProbeAsync(ShardHarness shard, ShardSearchMethod method) =>
        StrictHandlingProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, method, Ct);

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task ControlThenChallenge_AnsweredWith400_IsProvenForThatMethodAndNothingElse(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400, "Synthetic diagnostic; not a search page"));

        StrictHandlingProbeResult result = await ProbeAsync(shard, method);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.Proven);
        result.IsProven.ShouldBeTrue();
        result.Shard.ShouldBe(shard.Shard.Id);
        result.Type.ShouldBe(ShardHarness.Patient);
        result.Method.ShouldBe(method);
        shard.Server.Requests.Select(ProbeScript.Wire).ShouldBe(
            ["GET /fhir/metadata", ProbeScript.ControlWire(method), ProbeScript.ChallengeWire(method)]);
        foreach (RecordedRequest sent in shard.Server.Requests.Skip(1))
        {
            sent.HeaderValues("Prefer").ShouldBe(["handling=strict"]);
            sent.HeaderValues("Accept").ShouldBe(["application/fhir+json"]);
            sent.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
        }

        shard.Meter.Sends.Count.ShouldBe(3); // the metadata fetch and the two searches, all through the client's own meter
    }

    [Fact]
    public async Task PostProbe_DoesNotUseAGetResult_AndAGetProbeDoesNotUseAPostResult()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.PatientAdvertisement(), ProbeScript.Control(shard), ScriptedResponse.Status(400),
            ProbeScript.PatientAdvertisement(), ProbeScript.Control(shard), ProbeScript.EchoedChallenge(shard));

        StrictHandlingProbeResult get = await ProbeAsync(shard, ShardSearchMethod.Get);
        StrictHandlingProbeResult post = await ProbeAsync(shard, ShardSearchMethod.PostSearch);

        get.IsProven.ShouldBeTrue();
        post.IsProven.ShouldBeFalse();
        post.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeAccepted);
        shard.Server.Requests.Select(r => r.Method).ShouldBe(["GET", "GET", "GET", "GET", "POST", "POST"]);
    }

    [Fact]
    public async Task PostProbe_OnAProfileWithoutPost_SendsNothing()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.PostSearch);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.MethodNotOffered);
        result.IsProven.ShouldBeFalse();
        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GetProbe_OnAProfileWithoutPost_IsStillProven()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));

        (await ProbeAsync(shard, ShardSearchMethod.Get)).IsProven.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task Control_IsBoundedToOnePage_AndItsNextLinkIsNeverFollowed(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.PatientAdvertisement(),
            ProbeScript.Control(shard, next: shard.Search + "?_count=1&ct=Mg"),
            ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, method);

        result.IsProven.ShouldBeTrue();
        shard.Server.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(new[] { "ignixa-strict-probe" }, new string[0], "ignixa-strict-probe-1")]
    [InlineData(new string[0], new[] { "ignixa-strict-probe" }, "ignixa-strict-probe-1")]
    [InlineData(new[] { "ignixa-strict-probe" }, new[] { "ignixa-strict-probe-1" }, "ignixa-strict-probe-2")]
    [InlineData(new[] { "IGNIXA-STRICT-PROBE" }, new string[0], "ignixa-strict-probe-1")]
    public async Task Challenge_UsesAParameterNeitherTheCommonNorTheResourceAdvertisement_Lists(string[] common, string[] resource, string expected)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.Advertisement(common, ("Patient", [.. resource, "name"])),
            ProbeScript.Control(shard),
            ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.IsProven.ShouldBeTrue();
        ProbeScript.Wire(shard.Server.Requests[^1]).ShouldBe(ProbeScript.ChallengeWire(ShardSearchMethod.Get, expected));
    }

    [Fact]
    public async Task Challenge_IgnoresParametersAdvertisedOnlyForOtherResourceTypes()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.Advertisement(null, ("Patient", ["name"]), ("Observation", [ProbeScript.Code])),
            ProbeScript.Control(shard),
            ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.IsProven.ShouldBeTrue();
        ProbeScript.Wire(shard.Server.Requests[^1]).ShouldBe(ProbeScript.ChallengeWire(ShardSearchMethod.Get));
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task ChallengeAnswered200_EvenWithTheParameterDroppedFromSelf_IsNotProof(ShardSearchMethod method)
    {
        await using var accepted = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(accepted, ProbeScript.EchoedChallenge(accepted));
        await using var dropped = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(dropped, ProbeScript.DroppedChallenge(dropped));

        StrictHandlingProbeResult ignored = await ProbeAsync(accepted, method);
        StrictHandlingProbeResult selfDropped = await ProbeAsync(dropped, method);

        ignored.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeAccepted);
        selfDropped.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        selfDropped.IsProven.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ShardSearchMethod.Get, 401)]
    [InlineData(ShardSearchMethod.Get, 404)]
    [InlineData(ShardSearchMethod.Get, 422)]
    [InlineData(ShardSearchMethod.Get, 302)]
    [InlineData(ShardSearchMethod.Get, 204)]
    [InlineData(ShardSearchMethod.PostSearch, 422)]
    [InlineData(ShardSearchMethod.PostSearch, 500)]
    public async Task ChallengeAnsweredWithAnyOtherStatus_IsNotProof(ShardSearchMethod method, int status)
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(status));

        StrictHandlingProbeResult result = await ProbeAsync(shard, method);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task GetChallenge_ThatFailsTransientlyThenIsAnswered400OnTheRetry_IsNotProof(int firstStatus)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.PatientAdvertisement(), ProbeScript.Control(shard), ScriptedResponse.Status(firstStatus), ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task GetChallenge_WhoseConnectionIsDroppedThenIsAnswered400OnTheRetry_IsNotProof()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.PatientAdvertisement(), ProbeScript.Control(shard), ScriptedResponse.Hangup(), ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Count.ShouldBe(4);
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task FailedControl_PreventsTheChallenge(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ProbeScript.PatientAdvertisement(), ScriptedResponse.Status(400), ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, method);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ControlFailed);
        result.IsProven.ShouldBeFalse();
        shard.Server.Requests.Select(ProbeScript.Wire).ShouldBe(["GET /fhir/metadata", ProbeScript.ControlWire(method)]);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(401)]
    public async Task ControlAnsweredWithAnyNon200_PreventsTheChallenge(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ProbeScript.PatientAdvertisement(), ScriptedResponse.Status(status), ScriptedResponse.Status(400), ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ControlFailed);
        shard.Server.Requests.Select(r => r.Target).ShouldNotContain(t => t.Contains(ProbeScript.Code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlWithAnUnfaithfulSelfLink_PreventsTheChallenge()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ProbeScript.PatientAdvertisement(),
            ScriptedResponse.Json(Searchsets.Page(shard.Search + "?_count=2")),
            ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ControlFailed);
        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ControlThatDoesNotFitTheShardsLimits_IsNeverSentAndPreventsTheChallenge()
    {
        await using var shard = ShardHarness.Start(limits: new ShardRequestLimits(maxGetTargetBytes: 8, maxPostBodyBytes: 8));
        shard.Server.Respond(ProbeScript.PatientAdvertisement());

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ControlFailed);
        shard.Server.Requests.Select(ProbeScript.Wire).ShouldBe(["GET /fhir/metadata"]);
    }

    [Fact]
    public async Task ChallengeThatDoesNotFitTheShardsLimits_IsNeverSent()
    {
        // The control target is "/fhir/Patient?_count=1" (22 bytes); the challenge adds the parameter and so is longer.
        await using var shard = ShardHarness.Start(limits: new ShardRequestLimits(maxGetTargetBytes: 22, maxPostBodyBytes: 8));
        shard.Server.Respond(ProbeScript.PatientAdvertisement(), ProbeScript.Control(shard));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Select(ProbeScript.Wire).ShouldBe(["GET /fhir/metadata", "GET /fhir/Patient?_count=1"]);
    }

    [Fact]
    public async Task TypeThatIsNotAdvertisedForSearch_IsNotAdvertised_AndNothingIsSearched()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ProbeScript.Advertisement(null, ("Observation", ["code"])));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.NotAdvertised);
        shard.Server.Requests.Select(ProbeScript.Wire).ShouldBe(["GET /fhir/metadata"]);
    }

    [Fact]
    public async Task ResourceAdvertisedWithoutSearchType_IsNotAdvertised()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Capabilities.CapabilityJson.ServerStatement(
            Capabilities.CapabilityJson.Arr("{\"type\":\"Patient\",\"interaction\":[{\"code\":\"read\"}]}"))));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.NotAdvertised);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    public async Task AdvertisementThatCannotBeFetched_IsUnavailable_AndNothingIsSearched(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(status));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.AdvertisementUnavailable);
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AdvertisementThatIsNotACapabilityStatement_IsUnavailable()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json("{\"resourceType\":\"Bundle\"}"));

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.AdvertisementUnavailable);
    }

    [Fact]
    public async Task Probe_OnTheRoutersOwnClient_UsesTheSameTransportAndCredential()
    {
        await using var rig = MultiShardRig.Start();
        rig.East.Respond(ProbeScript.PatientAdvertisement(), ScriptedResponse.Json(Searchsets.Page(rig.East.BaseUrl + "/Patient?_count=1")), ScriptedResponse.Status(400));

        StrictHandlingProbeResult result = await StrictHandlingProbe.ProbeAsync(
            rig.Router.GetClient(rig.EastShard), rig.EastShard, ShardHarness.Patient, ShardSearchMethod.Get, Ct);

        result.IsProven.ShouldBeTrue();
        result.Shard.ShouldBe(MultiShardRig.EastId);
        rig.West.Requests.ShouldBeEmpty();
        rig.Meter.Sends.ShouldBe([MultiShardRig.EastId, MultiShardRig.EastId, MultiShardRig.EastId]);
    }

    [Fact]
    public async Task Probe_WithADescriptorThatIsNotTheClientsOwn_IsRefusedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start();
        await using var other = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentException>(
            () => StrictHandlingProbe.ProbeAsync(shard.Client, other.Shard, ShardHarness.Patient, ShardSearchMethod.Get, Ct));

        shard.Server.Requests.ShouldBeEmpty();
        shard.Credentials.Calls.ShouldBe(0);
    }

    // A real 400 from another shard: the shape a credential provider or meter could rethrow as its own failure.
    private static async Task<ShardCallException> RealBadRequestAsync()
    {
        await using var other = ShardHarness.Start();
        other.Server.Respond(ScriptedResponse.Status(400));
        ShardCallException bad = await Should.ThrowAsync<ShardCallException>(() => other.Client.SearchAsync(other.SarahRequest(), Ct));
        bad.StatusCode.ShouldBe(400);
        return bad;
    }

    [Fact]
    public async Task ABadRequestRethrownByTheCredentialProvider_IsNotProofThatTheShardAnsweredIt()
    {
        ShardCallException foreign = await RealBadRequestAsync();
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        shard.Credentials.Acquire = _ => shard.Credentials.Calls == 3 ? throw foreign : ValueTask.FromResult(SyntheticCredentials.Token);

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.Get);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ABadRequestRethrownByTheMeter_IsNotProofThatTheShardAnsweredIt()
    {
        ShardCallException foreign = await RealBadRequestAsync();
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        shard.Meter.ThrowOnSend = foreign;
        shard.Meter.ThrowFromSend = 3; // the metadata fetch and the control are sends 1 and 2

        StrictHandlingProbeResult result = await ProbeAsync(shard, ShardSearchMethod.PostSearch);

        result.Outcome.ShouldBe(StrictHandlingProbeOutcome.ChallengeNotProven);
        shard.Server.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ACollaboratorFailureThatIsNotAShardCallFailure_PropagatesAndYieldsNoEvidence()
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        shard.Credentials.Acquire = _ => shard.Credentials.Calls == 3
            ? throw new InvalidOperationException("provider bug")
            : ValueTask.FromResult(SyntheticCredentials.Token);

        await Should.ThrowAsync<InvalidOperationException>(() => ProbeAsync(shard, ShardSearchMethod.Get));
    }

    [Fact]
    public async Task AnUndefinedMethod_IsRefusedBeforeAcquiringCredentialsOrSendingAnything()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ProbeScript.PatientAdvertisement());

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => ProbeAsync(shard, (ShardSearchMethod)99));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task ACancelledToken_IsThrownBeforeAnythingIsSent_EvenForAnUnsupportedMethod(ShardSearchMethod method)
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => StrictHandlingProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, method, cts.Token));

        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationDuringTheChallenge_IsThrown_NotReportedAsAnOutcome()
    {
        await using var shard = ShardHarness.Start();
        var hold = new ResponseHold();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<StrictHandlingProbeResult> probe = StrictHandlingProbe.ProbeAsync(
            shard.Client, shard.Shard, ShardHarness.Patient, ShardSearchMethod.Get, cts.Token);
        await hold.ArrivalAsync(Ct);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => probe);
    }
}

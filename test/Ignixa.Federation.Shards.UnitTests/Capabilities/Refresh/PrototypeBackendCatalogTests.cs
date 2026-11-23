using System.Collections.Immutable;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Ignixa.Federation.Shards.UnitTests.Probes;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// The pinned prototype catalog as a host sees it: two loopback shards at the real transport, serving the pinned captures'
/// reduced advertisements, admitted by the production probes, and the planner input and read ceiling that result.
/// </summary>
public sealed class PrototypeBackendCatalogTests
{
    private const string Patient = "Patient";

    private const NextLinkRedemption Follow = NextLinkRedemption.FollowGet;

    private const string Read = """{ "code": "read" }""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, bool> RefusalsBeforeAnyProbe
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (bool onWest in new[] { false, true })
            {
                foreach (string scenario in new[]
                {
                    "another version", "malformed version", "another product", "no read", "vread only", "modified read", "no search-type",
                    "no given", "conflicting _id", "no server mode",
                })
                {
                    data.Add(scenario, onWest);
                }
            }

            return data;
        }
    }

    private static string[] SuccessfulWires(ShardSearchMethod method = ShardSearchMethod.Get, NextLinkRedemption redemption = Follow) =>
    [
        ProbeRoutes.ControlWire(Patient, method),
        ProbeRoutes.ChallengeWire(Patient, method),
        ProbeRoutes.StartWire(Patient, method),
        ProbeRoutes.RedeemWire(Patient, method, redemption),
        ProbeRoutes.RedeemWire(Patient, method, redemption),
    ];

    [Fact]
    public void Profiles_AreTheTwoPinnedGetOnlyBackendsWithNoCertifiedOrder()
    {
        ImmutableArray<BackendProfile> profiles = PrototypeBackendCatalog.Profiles;

        profiles.Select(p => p.Name).ShouldBe(["Ignixa-src-2f07ac88", "MicrosoftFhirServer-src-2a67597a"]);
        profiles.ShouldAllBe(p => !p.SupportsPostSearch && p.MaxCount == 1 && p.LastUpdatedOrder == null && p.NextLinkRedemption == Follow);
        PrototypeBackendCatalog.Profiles[0].ShouldBeSameAs(profiles[0], "one immutable instance, recognized by reference");
    }

    [Fact]
    public async Task BothPinnedBackends_AreAdmitted_AfterOneProbeExchangeEach_WithNoSecondMetadataFetch()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();

        PrototypeBackendAdmission? admission = await rig.AdmitAsync(Ct);

        admission.ShouldNotBeNull();
        rig.EastWires.ShouldBe([ProbeRoutes.Metadata, .. SuccessfulWires()]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata, .. SuccessfulWires()]);
        admission.IsCurrent().ShouldBeTrue();
        admission.SearchCapabilities.Registry.ShouldBeSameAs(rig.Registry);
        admission.SearchCapabilities.SupportsLastUpdatedSort.ShouldBeFalse("sampling is not sort certification");
    }

    [Fact]
    public async Task Admission_PublishesPatientIdAndGivenOnly_NotTheBroadAdvertisement()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();

        PrototypeBackendAdmission admission = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();

        ResourceSearchSupport patient = admission.SearchCapabilities.Resources.Single();
        patient.Type.Value.ShouldBe(Patient);
        patient.Parameters.Select(p => p.Code).ShouldBe(["_id", "given"], "name and family are advertised but not admitted");
        patient.SearchIncludes.ShouldBeEmpty();
        patient.SearchRevIncludes.ShouldBeEmpty();
        admission.ReadableResourceTypes.ShouldBe([ResourceType.Parse(Patient)], "Observation read is advertised but not admitted");
        rig.EastWires.ShouldAllBe(wire => !wire.Contains("Observation"), "no negative control is probed");
        rig.WestWires.ShouldAllBe(wire => !wire.Contains("Observation"));
    }

    [Fact]
    public async Task Planner_AcceptsTheAdmittedShapeAndRefusesEverythingNotMeasured()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        SearchPlanner planner = Planner((await rig.AdmitAsync(Ct)).ShouldNotBeNull());

        Plan(planner, Patient, "_id=1&given=Jones").Plan.ShouldNotBeNull();
        Plan(planner, Patient, "family=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
        Plan(planner, Patient, "name=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
        Plan(planner, Patient, "given:exact=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedModifier);
        Plan(planner, Patient, "given=a&given=b").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedRepetition);
        Plan(planner, Patient, "_id=1,2").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedAlternatives);
        Plan(planner, Patient, "_id=1&_include=Patient:organization").Failure.ShouldNotBeNull();
        Plan(planner, Patient, "_count=0&_total=accurate").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.ExactCountNotCertified);
        Plan(planner, "Observation", "_id=1").Failure.ShouldNotBeNull("the advertised Observation search is a negative control");
    }

    [Theory]
    [MemberData(nameof(RefusalsBeforeAnyProbe))]
    public async Task AnAdvertisementThatFailsTheCeiling_IsRefused_BeforeAnyProbeIsSent(string scenario, bool onWest)
    {
        string east = onWest ? PrototypeFixtures.East : Mutate(PrototypeFixtures.East, scenario);
        string west = onWest ? Mutate(PrototypeFixtures.West, scenario) : PrototypeFixtures.West;
        await using PrototypeRig rig = await PrototypeRig.StartAsync(eastStatement: east, westStatement: west);
        rig.SucceedEverywhere();

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task ABroadAdvertisementOfOtherResources_DoesNotWidenAnythingAndIsNotRefused()
    {
        string east = ReplaceLast(PrototypeFixtures.East, Read, """{ "code": "read", "modifierExtension": [{ "url": "http://example.test/modifier", "valueBoolean": true }] }""");
        await using PrototypeRig rig = await PrototypeRig.StartAsync(eastStatement: east);
        rig.SucceedEverywhere();

        PrototypeBackendAdmission admission = (await rig.AdmitAsync(Ct)).ShouldNotBeNull("a modified Observation read grants nothing and costs nothing");

        admission.ReadableResourceTypes.ShouldBe([ResourceType.Parse(Patient)]);
    }

    [Fact]
    public async Task EachBackendMustCarryItsOwnPinnedIdentity_SoSwappedProfilesAreRefused()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync(
            east: PrototypeBackendCatalog.Profiles[1],
            west: PrototypeBackendCatalog.Profiles[0]);
        rig.SucceedEverywhere();

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task ACopyOfAPinnedProfile_EvenWithTheSameNameAndValues_IsNotThePinnedProfile()
    {
        BackendProfile copy = PrototypeBackendCatalog.Profiles[0] with { };
        copy.ShouldBe(PrototypeBackendCatalog.Profiles[0]);
        await using PrototypeRig rig = await PrototypeRig.StartAsync(east: copy);
        rig.SucceedEverywhere();

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task AnOperatorProfileUnderAnotherName_IsNotAdmitted()
    {
        var other = new BackendProfile("SomeOtherBackend", SupportsPostSearch: false, MaxCount: 1, Follow, LastUpdatedOrder: null);
        await using PrototypeRig rig = await PrototypeRig.StartAsync(east: other);
        rig.SucceedEverywhere();

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task ARouterOverAnotherRegistryInstance_IsRefused_AndSendsNothing()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        using MultiShardHttpClient foreign = PrototypeRig.Route(rig.CreateRegistry());

        (await PrototypeBackendCatalog.TryAdmitAsync(foreign, rig.Refresher, Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task EvidenceTheStandaloneParserProduced_IsRefused_EvenWhenItReadsAsTheAdvertisement()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync(
            fetch: _ => (shard, _) => Task.FromResult(CapabilityJson.Read(shard.Id.Value == "east" ? PrototypeFixtures.East : PrototypeFixtures.West)));
        rig.SucceedEverywhere();

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBeEmpty();
        rig.WestWires.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADisposedRouter_Throws_RatherThanReportingARefusal()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.Router.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(() => rig.AdmitAsync(Ct));
    }

    [Fact]
    public async Task ArgumentsAreRequired()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();

        await Should.ThrowAsync<ArgumentNullException>(() => PrototypeBackendCatalog.TryAdmitAsync(null!, rig.Refresher, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => PrototypeBackendCatalog.TryAdmitAsync(rig.Router, null!, Ct));
    }

    [Fact]
    public async Task StrictChallengeAnswered200_IsRefused_AndTheSecondBackendIsNeverProbed()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.EastRoutes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Json(Searchsets.Page(rig.East.BaseUrl + $"/Patient?{ProbeScript.Code}=1&_count=1")));
        rig.WestRoutes.Succeed(Patient, ShardSearchMethod.Get, Follow);

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata, ProbeRoutes.ControlWire(Patient, ShardSearchMethod.Get), ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.Get)]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task ReplayThatCannotBeObservedOnTheSecondBackend_IsRefused_AfterTheFirstWasProbed()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.EastRoutes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        rig.WestRoutes.Strict(Patient, ShardSearchMethod.Get);
        rig.WestRoutes.Replay(Patient, ShardSearchMethod.Get, Follow, first: rig.WestRoutes.FirstPage(Patient, ShardSearchMethod.Get, withNext: false));

        (await rig.AdmitAsync(Ct)).ShouldBeNull();

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata, .. SuccessfulWires()]);
        rig.WestWires.Last().ShouldBe(ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get));
    }

    [Fact]
    public async Task FailedRefresh_KeepsTheEvidence_SoTheAdmissionStaysCurrent_AndAdmitsAgain()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        PrototypeBackendAdmission earlier = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
        rig.EastRoutes.Advertisement = ScriptedResponse.Status(503);
        rig.WestRoutes.Advertisement = ScriptedResponse.Status(503);

        await rig.RefreshAsync();

        rig.Refresher.Current.IsDegraded.ShouldBeTrue();
        earlier.IsCurrent().ShouldBeTrue();
        rig.SucceedEverywhere();
        (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SuccessfulIdenticalRefresh_EndsTheAdmission_UntilAnotherIsGranted()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        PrototypeBackendAdmission earlier = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();

        await rig.RefreshAsync();

        earlier.IsCurrent().ShouldBeFalse("the refetched evidence is a new instance, even with identical content");
        rig.SucceedEverywhere();
        PrototypeBackendAdmission replacement = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
        replacement.IsCurrent().ShouldBeTrue();
        earlier.IsCurrent().ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingEitherShardsEvidence_EndsTheAdmission(bool retainWest)
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        PrototypeBackendAdmission admission = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
        admission.IsCurrent().ShouldBeTrue();

        await RefreshOnlyOneAdvertisementAsync(rig, retainWest);

        admission.IsCurrent().ShouldBeFalse();
    }

    [Fact]
    public async Task ContradictoryRefresh_EndsTheAdmission_AndTheNewAdvertisementIsRefusedWithoutProbes()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        PrototypeBackendAdmission earlier = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
        rig.EastRoutes.Advertisement = ScriptedResponse.Json(Mutate(PrototypeFixtures.East, "no read"));

        await rig.RefreshAsync();
        rig.SucceedEverywhere();

        earlier.IsCurrent().ShouldBeFalse();
        (await rig.AdmitAsync(Ct)).ShouldBeNull();
        rig.EastWires.Count(wire => wire == ProbeRoutes.ControlWire(Patient, ShardSearchMethod.Get)).ShouldBe(1, "no probe followed the contradictory advertisement");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshPublishedWhileProbing_RefusesTheStaleEvidence_ThenTheReplacementIsAdmitted(bool whileProbingLastShard)
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        var hold = new ResponseHold();
        ProbeRoutes routes = whileProbingLastShard ? rig.WestRoutes : rig.EastRoutes;
        ProbeRoutes other = whileProbingLastShard ? rig.EastRoutes : rig.WestRoutes;
        routes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        routes.Replay(Patient, ShardSearchMethod.Get, Follow);
        other.Succeed(Patient, ShardSearchMethod.Get, Follow);

        Task<PrototypeBackendAdmission?> admission = rig.AdmitAsync(Ct);
        await hold.ArrivalAsync(Ct);
        await rig.RefreshAsync();
        hold.Release();

        (await admission).ShouldBeNull("every probe may pass, but only against evidence that is no longer current");
        rig.SucceedEverywhere();
        PrototypeBackendAdmission replacement = (await rig.AdmitAsync(Ct)).ShouldNotBeNull();
        replacement.IsCurrent().ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacingEitherShardsEvidence_DuringTheLastShardProbe_RefusesAdmission(bool retainWest)
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        var hold = new ResponseHold();
        rig.EastRoutes.Succeed(Patient, ShardSearchMethod.Get, Follow);
        rig.WestRoutes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        rig.WestRoutes.Replay(Patient, ShardSearchMethod.Get, Follow);

        Task<PrototypeBackendAdmission?> admission = rig.AdmitAsync(Ct);
        try
        {
            await hold.ArrivalAsync(Ct);
            await RefreshOnlyOneAdvertisementAsync(rig, retainWest);
        }
        finally
        {
            hold.Release();
        }

        (await admission).ShouldBeNull("one replaced advertisement invalidates the combined view even after all probes pass");
        rig.EastWires.Where(wire => wire != ProbeRoutes.Metadata).ShouldBe(SuccessfulWires());
        rig.WestWires.Where(wire => wire != ProbeRoutes.Metadata).ShouldBe(SuccessfulWires());
    }

    [Fact]
    public async Task ACancelledToken_IsThrown_BeforeAnythingIsSent()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        rig.SucceedEverywhere();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => rig.AdmitAsync(cts.Token));

        rig.EastWires.ShouldBe([ProbeRoutes.Metadata]);
        rig.WestWires.ShouldBe([ProbeRoutes.Metadata]);
    }

    [Fact]
    public async Task CancellationDuringAProbe_IsThrown_NotReportedAsARefusal()
    {
        await using PrototypeRig rig = await PrototypeRig.StartAsync();
        var hold = new ResponseHold();
        rig.EastRoutes.Strict(Patient, ShardSearchMethod.Get, challenge: ScriptedResponse.Status(400, beforeHead: hold.BeforeHead));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<PrototypeBackendAdmission?> admission = rig.AdmitAsync(cts.Token);
        await hold.ArrivalAsync(Ct);
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => admission);
    }

    private static async Task RefreshOnlyOneAdvertisementAsync(PrototypeRig rig, bool retainWest)
    {
        var (retainedRoutes, retained, replaced) = retainWest
            ? (rig.WestRoutes, rig.WestShard, rig.EastShard)
            : (rig.EastRoutes, rig.EastShard, rig.WestShard);
        ShardCapabilitySnapshot before = rig.Refresher.Current;
        retainedRoutes.Advertisement = ScriptedResponse.Status(503);

        await rig.RefreshAsync();

        ShardCapabilitySnapshot after = rig.Refresher.Current;
        after.Shards[retained.Id].Failure.ShouldNotBeNull();
        after.Shards[retained.Id].Evidence.ShouldBeSameAs(before.Shards[retained.Id].Evidence.ShouldNotBeNull());
        after.Shards[replaced.Id].Failure.ShouldBeNull();
        after.Shards[replaced.Id].Evidence.ShouldNotBeNull().ShouldNotBeSameAs(before.Shards[replaced.Id].Evidence.ShouldNotBeNull());
    }

    private static string Mutate(string statement, string scenario) => scenario switch
    {
        "another version" => statement.Contains("\"version\": \"1.0.0\"", StringComparison.Ordinal)
            ? Replace(statement, "\"version\": \"1.0.0\"", "\"version\": \"1.0.1\"")
            : Replace(statement, ".Sha.2f07ac88", ".Sha.3f07ac88"),
        "malformed version" => Replace(statement, "\"version\": \"", "\"version\": \"9."),
        "another product" => Replace(statement, "FHIR Server", "FHIR Servr"),
        "no read" => Replace(statement, Read, """{ "code": "history-type" }"""),
        "vread only" => Replace(statement, Read, """{ "code": "vread" }"""),
        "modified read" => Replace(statement, Read, """{ "code": "read", "modifierExtension": [{ "url": "http://example.test/modifier", "valueBoolean": true }] }"""),
        "no search-type" => Replace(statement, """{ "code": "search-type" }""", """{ "code": "history-type" }"""),
        "no given" => Replace(statement, "\"name\": \"given\"", "\"name\": \"given-name\""),
        "conflicting _id" => Replace(statement, "\"name\": \"_id\", \"type\": \"token\"", "\"name\": \"_id\", \"type\": \"string\""),
        "no server mode" => Replace(statement, "\"mode\": \"server\"", "\"mode\": \"client\""),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    // Each edit must find its text, or a changed fixture would make a refusal test pass for no reason.
    private static string Replace(string text, string find, string replacement)
    {
        int at = text.IndexOf(find, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"the fixture has no {find}");
        return text[..at] + replacement + text[(at + find.Length)..];
    }

    private static string ReplaceLast(string text, string find, string replacement)
    {
        int at = text.LastIndexOf(find, StringComparison.Ordinal);
        at.ShouldBeGreaterThanOrEqualTo(0, $"the fixture has no {find}");
        return text[..at] + replacement + text[(at + find.Length)..];
    }

    private static SearchPlanner Planner(PrototypeBackendAdmission admission) =>
        new(admission.SearchCapabilities, new ChainResolutionOptions(), new LimitOptions());

    private static SearchPlanResult Plan(SearchPlanner planner, string type, string query) =>
        planner.Plan(ResourceType.Parse(type), SearchQuery.Parse(query).Query.ShouldNotBeNull());
}

using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// The startup probes as a host sees them: a Generic Host built from the product registrations, real loopback TLS shards, the
/// shared router's own clients, and the <c>ready</c> health check that reports what the probes showed (design spec §6.2, §7.4,
/// §9.2). Every wait is a gate or an observable completion, never a delay.
/// </summary>
public sealed class ShardStartupProbeTests
{
    private const string Patient = "Patient";
    private const string Get = "Get";
    private const string Post = "PostSearch";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Datum(HealthReportEntry entry, string shard, string method) =>
        (string)entry.Data[$"{shard}/{method}"];

    [Fact]
    public async Task Untested_IsNotReady_AndReadingHealthSendsNothing()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        var hold = new ResponseHold();
        fixture.EastRoutes.Strict(Patient, ShardSearchMethod.Get, control: ScriptedResponse.Json(
            Searchsets.Page(fixture.East.BaseUrl + "/Patient?_count=1"), hold.BeforeHead));
        fixture.EastRoutes.Replay(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet);
        fixture.WestRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await hold.ArrivalAsync(Ct);
        await fixture.AwaitRefreshAsync();
        int before = fixture.Requests;

        for (int i = 0; i < 3; i++)
        {
            HealthReportEntry inFlight = await fixture.CheckAsync(Ct);
            inFlight.Status.ShouldBe(HealthStatus.Unhealthy);
            inFlight.Tags.ShouldContain("ready");
            Datum(inFlight, "east", Get).ShouldBe("strict=NotTested;replay=NotTested");
            Datum(inFlight, "east", Post).ShouldBe("strict=NotTested;replay=NotTested");
            Datum(inFlight, "west", Get).ShouldBe("strict=NotTested;replay=NotTested");
            inFlight.Data.ContainsKey($"west/{Post}").ShouldBeFalse();
        }

        fixture.Requests.ShouldBe(before);

        hold.Release();
        await fixture.AwaitPassAsync();

        (await fixture.CheckAsync(Ct)).Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task EveryRequiredMethodProven_IsReady_AndEachCursorIsRedeemedTwiceByItsPolicy()
    {
        // east offers POST and reposts a POST cursor; west is GET-only, so only GET is required of it.
        await using var fixture = new StartupProbeHost(ShardHarness.Repost, ShardHarness.GetOnly);
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.RepostToSearch);
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.PostSearch, NextLinkRedemption.RepostToSearch);
        fixture.WestRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Exception.ShouldBeNull();
        Datum(entry, "east", Get).ShouldBe("strict=Proven;replay=Observed");
        Datum(entry, "east", Post).ShouldBe("strict=Proven;replay=Observed");
        Datum(entry, "west", Get).ShouldBe("strict=Proven;replay=Observed");
        entry.Data.Keys.ShouldBe([$"east/{Get}", $"east/{Post}", $"west/{Get}"], ignoreOrder: true);

        // Per method: positive control, challenge, the first page, then that one cursor redeemed twice, by the profile's policy.
        StartupProbeHost.ProbeWires(fixture.East).ShouldBe(
        [
            ProbeRoutes.ControlWire(Patient, ShardSearchMethod.Get),
            ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.Get),
            ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get),
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=G",
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=G",
            ProbeRoutes.ControlWire(Patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.StartWire(Patient, ShardSearchMethod.PostSearch),
            "POST /fhir/Patient/_search _sort=_lastUpdated&_count=1&ct=P",
            "POST /fhir/Patient/_search _sort=_lastUpdated&_count=1&ct=P",
        ]);

        // A shard whose profile has no POST search is never sent a POST.
        StartupProbeHost.ProbeWires(fixture.West).ShouldBe(
        [
            ProbeRoutes.ControlWire(Patient, ShardSearchMethod.Get),
            ProbeRoutes.ChallengeWire(Patient, ShardSearchMethod.Get),
            ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get),
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=G",
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=G",
        ]);
    }

    [Fact]
    public async Task FollowPolicy_RedeemsAPostStartedCursorByGet()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet);
        fixture.WestRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        (await fixture.CheckAsync(Ct)).Status.ShouldBe(HealthStatus.Healthy);
        StartupProbeHost.ProbeWires(fixture.East).TakeLast(3).ShouldBe(
        [
            ProbeRoutes.StartWire(Patient, ShardSearchMethod.PostSearch),
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=P",
            "GET /fhir/Patient?_sort=_lastUpdated&_count=1&ct=P",
        ]);
    }

    [Fact]
    public async Task ShardsAndMethodsKeepDistinctOutcomes_AndOneNegativeNeverEndsTheOthers()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);

        // east GET: a shard that ignores the unadvertised parameter. east POST: fully shown.
        fixture.EastRoutes.Strict(
            Patient,
            ShardSearchMethod.Get,
            challenge: ScriptedResponse.Json(Searchsets.Page(fixture.East.BaseUrl + $"/Patient?{ProbeScript.Code}=1&_count=1")));
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet);

        // west GET: strict handling proven, but its first page is refused.
        fixture.WestRoutes.Strict(Patient, ShardSearchMethod.Get);
        fixture.WestRoutes.Replay(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet, first: ScriptedResponse.Status(404));
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Datum(entry, "east", Get).ShouldBe("strict=ChallengeAccepted;replay=NotTested");
        Datum(entry, "east", Post).ShouldBe("strict=Proven;replay=Observed");
        Datum(entry, "west", Get).ShouldBe("strict=Proven;replay=ReadFailed");

        // Replay is attempted only behind strict evidence for that same method.
        StartupProbeHost.ProbeWires(fixture.East).ShouldNotContain(ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get));
        StartupProbeHost.ProbeWires(fixture.East).ShouldContain(ProbeRoutes.StartWire(Patient, ShardSearchMethod.PostSearch));
        StartupProbeHost.ProbeWires(fixture.West).ShouldContain(ProbeRoutes.StartWire(Patient, ShardSearchMethod.Get));
    }

    public enum Negative
    {
        ChallengeAccepted,
        ControlRefused,
        NotAdvertised,
        AdvertisementRefused,
        FirstPageRefused,
        NoCursor,
        ReplayDiffered,
    }

    public static TheoryData<Negative, string> Negatives => new()
    {
        { Negative.ChallengeAccepted, "strict=ChallengeAccepted;replay=NotTested" },
        { Negative.ControlRefused, "strict=ControlFailed;replay=NotTested" },
        { Negative.NotAdvertised, "strict=NotAdvertised;replay=NotTested" },
        { Negative.AdvertisementRefused, "strict=AdvertisementUnavailable;replay=NotTested" },
        { Negative.FirstPageRefused, "strict=Proven;replay=ReadFailed" },
        { Negative.NoCursor, "strict=Proven;replay=InsufficientRows" },
        { Negative.ReplayDiffered, "strict=Proven;replay=ReplayDiffered" },
    };

    [Theory]
    [MemberData(nameof(Negatives))]
    public async Task AnyRequiredMethodNotShown_IsNotReady_WhileTheNextShardIsStillProbed(Negative negative, string expected)
    {
        await using var fixture = new StartupProbeHost(ShardHarness.GetOnly, ShardHarness.GetOnly);
        ProbeRoutes east = fixture.EastRoutes;
        const ShardSearchMethod get = ShardSearchMethod.Get;
        const NextLinkRedemption follow = NextLinkRedemption.FollowGet;
        switch (negative)
        {
            case Negative.ChallengeAccepted:
                east.Strict(Patient, get, challenge: ScriptedResponse.Json(Searchsets.Page(fixture.East.BaseUrl + $"/Patient?{ProbeScript.Code}=1&_count=1")));
                break;
            case Negative.ControlRefused:
                east.Strict(Patient, get, control: ScriptedResponse.Status(404));
                break;
            case Negative.NotAdvertised:
                east.Advertisement = ProbeScript.Advertisement(null, ("Observation", ["code"]));
                break;
            case Negative.AdvertisementRefused:
                east.Advertisement = ScriptedResponse.Status(404);
                break;
            case Negative.FirstPageRefused:
                east.Strict(Patient, get);
                east.Replay(Patient, get, follow, first: ScriptedResponse.Status(404));
                break;
            case Negative.NoCursor:
                east.Strict(Patient, get);
                east.Replay(Patient, get, follow, first: east.FirstPage(Patient, get, withNext: false));
                break;
            case Negative.ReplayDiffered:
                east.Strict(Patient, get);
                east.Replay(Patient, get, follow, firstRedemption: ProbeRoutes.Redeemed(Patient, "b1"), secondRedemption: ProbeRoutes.Redeemed(Patient, "b2"));
                break;
        }

        fixture.WestRoutes.Succeed(Patient, get, follow);
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Datum(entry, "east", Get).ShouldBe(expected);
        Datum(entry, "west", Get).ShouldBe("strict=Proven;replay=Observed");
        StartupProbeHost.ProbeWires(fixture.West).ShouldContain(ProbeRoutes.StartWire(Patient, get));
    }

    [Fact]
    public async Task ProbesTheSelectedTypePerShard_FromAMapTheHostMayStillEdit()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.GetOnly, ShardHarness.GetOnly);
        const string observation = "Observation";
        fixture.EastRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.WestRoutes.Advertisement = ProbeScript.Advertisement(null, ("Patient", ["name"]), (observation, ["code"]));
        fixture.WestRoutes.Succeed(observation, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);

        var selection = new Dictionary<ShardId, ResourceType>
        {
            [fixture.EastId] = StartupProbeHost.Patient,
            [fixture.WestId] = ResourceType.Parse(observation),
        };
        fixture.BuildHost(services => services.AddShardStartupProbes(selection));
        selection.Clear();

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        (await fixture.CheckAsync(Ct)).Status.ShouldBe(HealthStatus.Healthy);
        StartupProbeHost.ProbeWires(fixture.East).ShouldAllBe(w => w.Contains("/fhir/Patient", StringComparison.Ordinal));
        StartupProbeHost.ProbeWires(fixture.West).ShouldAllBe(w => w.Contains("/fhir/Observation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoppingTheHost_CancelsTheProbeInFlight_JoinsTheWorker_AndSendsNothingMore()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.GetOnly, ShardHarness.GetOnly);
        var hold = new ResponseHold();
        fixture.EastRoutes.Strict(Patient, ShardSearchMethod.Get, control: ScriptedResponse.Json(
            Searchsets.Page(fixture.East.BaseUrl + "/Patient?_count=1"), hold.BeforeHead));
        fixture.WestRoutes.Succeed(Patient, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.BuildHost();

        await fixture.Host.StartAsync(Ct);
        await hold.ArrivalAsync(Ct);
        ShardStartupProbeWorker worker = fixture.Worker;
        worker.ExecuteTask!.IsCompleted.ShouldBeFalse();

        await fixture.Host.StopAsync(Ct).WaitAsync(Watchdog.Interval, Ct);

        worker.ExecuteTask.IsCanceled.ShouldBeTrue();
        StartupProbeHost.ProbeWires(fixture.West).ShouldBeEmpty();
        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Datum(entry, "east", Get).ShouldBe("strict=NotTested;replay=NotTested");
    }

    [Fact]
    public async Task AnUnexpectedCollaboratorFault_IsNotSwallowed_AndNeverMakesTheShardsReady()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.GetOnly, ShardHarness.GetOnly);
        var credentials = new SyntheticCredentials
        {
            Acquire = _ => throw new InvalidOperationException("synthetic provider bug"),
        };
        fixture.BuildHost(credentials: credentials, hostOptions: o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        await fixture.Host.StartAsync(Ct);

        await Should.ThrowAsync<InvalidOperationException>(fixture.AwaitPassAsync);
        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Datum(entry, "east", Get).ShouldBe("strict=NotTested;replay=NotTested");
        Datum(entry, "west", Get).ShouldBe("strict=NotTested;replay=NotTested");
    }
}

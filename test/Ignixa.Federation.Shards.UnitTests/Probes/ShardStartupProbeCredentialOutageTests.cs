using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// A credential outage at a probe exchange, through the registered one-shot worker in a Generic Host with the default
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> policy (design spec §8, §9.2). The capability refresher is not
/// registered, so the probes are the only caller of the provider and the outage lands on an exact exchange.
/// </summary>
public sealed class ShardStartupProbeCredentialOutageTests
{
    private const string Get = "Get";
    private const string Post = "PostSearch";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Datum(HealthReportEntry entry, string shard, string method) =>
        (string)entry.Data[$"{shard}/{method}"];

    [Fact]
    public async Task OneOutageAtEastGetControl_IsThatMethodsNegativeResult_AndEveryOtherMethodIsStillProbed()
    {
        await using var fixture = new StartupProbeHost(ShardHarness.Follow, ShardHarness.GetOnly);
        fixture.EastRoutes.Succeed(StartupProbeHost.Patient.Value, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);
        fixture.EastRoutes.Succeed(StartupProbeHost.Patient.Value, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet);
        fixture.WestRoutes.Succeed(StartupProbeHost.Patient.Value, ShardSearchMethod.Get, NextLinkRedemption.FollowGet);

        // The probes alone call the provider, in order: east GET metadata is acquisition 1 and its positive control is 2.
        var credentials = new SyntheticCredentials();
        credentials.Acquire = _ => credentials.Calls == 2
            ? throw new ShardCredentialException(ShardCredentialFailure.TokenUnavailable)
            : ValueTask.FromResult(SyntheticCredentials.Token);
        fixture.BuildProbeOnlyHost(credentials);

        await fixture.Host.StartAsync(Ct);
        await fixture.AwaitPassAsync();

        fixture.Worker.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        fixture.Host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested.ShouldBeFalse();

        HealthReportEntry entry = await fixture.CheckAsync(Ct);
        entry.Status.ShouldBe(HealthStatus.Unhealthy);
        Datum(entry, "east", Get).ShouldBe("strict=ControlFailed;replay=NotTested");
        Datum(entry, "east", Post).ShouldBe("strict=Proven;replay=Observed");
        Datum(entry, "west", Get).ShouldBe("strict=Proven;replay=Observed");

        // The failed exchange sent nothing; the other methods ran with their normal wire.
        string patient = StartupProbeHost.Patient.Value;
        StartupProbeHost.ProbeWires(fixture.East).ShouldBe(
        [
            ProbeRoutes.ControlWire(patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.ChallengeWire(patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.StartWire(patient, ShardSearchMethod.PostSearch),
            ProbeRoutes.RedeemWire(patient, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet),
            ProbeRoutes.RedeemWire(patient, ShardSearchMethod.PostSearch, NextLinkRedemption.FollowGet),
        ]);
        StartupProbeHost.ProbeWires(fixture.West).Length.ShouldBe(5);

        // east GET: metadata and the denied control; east POST and west GET: metadata plus five exchanges each.
        credentials.Calls.ShouldBe(2 + 6 + 6);
    }
}

using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.Probes;
using Ignixa.Federation.Shards.UnitTests.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// A typed credential outage while the startup probes acquire a token, over a real TLS loopback transport (design spec
/// §9.1, §9.2): the probe's exchange is a local, unsent, classified failure and so a negative result at exactly that
/// stage, while a caller's cancellation and any other collaborator fault keep their own exceptions, and the ordinary
/// search, redemption and read transport are unchanged.
/// </summary>
public sealed class ProbeCredentialOutageTests
{
    private const string Sort = "_sort=_lastUpdated&_count=1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ShardCredentialFailure> CredentialFailures => new(Enum.GetValues<ShardCredentialFailure>());

    // Strict probe: acquisition 1 is the metadata fetch, 2 the positive control, 3 the challenge.
    public static TheoryData<ShardSearchMethod, int, StrictHandlingProbeOutcome> StrictStages => new()
    {
        { ShardSearchMethod.Get, 2, StrictHandlingProbeOutcome.ControlFailed },
        { ShardSearchMethod.Get, 3, StrictHandlingProbeOutcome.ChallengeNotProven },
        { ShardSearchMethod.PostSearch, 2, StrictHandlingProbeOutcome.ControlFailed },
        { ShardSearchMethod.PostSearch, 3, StrictHandlingProbeOutcome.ChallengeNotProven },
    };

    // Replay probe: acquisition 1 is the sorted start, 2 the first redemption, 3 the second.
    public static TheoryData<ShardSearchMethod, int> ReplayStages => new()
    {
        { ShardSearchMethod.Get, 1 },
        { ShardSearchMethod.Get, 2 },
        { ShardSearchMethod.Get, 3 },
        { ShardSearchMethod.PostSearch, 1 },
        { ShardSearchMethod.PostSearch, 2 },
        { ShardSearchMethod.PostSearch, 3 },
    };

    private static ShardCredentialException Outage() => new(ShardCredentialFailure.TokenUnavailable);

    private static void FailAcquisition(ShardHarness shard, int call) =>
        shard.Credentials.Acquire = _ => shard.Credentials.Calls == call
            ? throw Outage()
            : ValueTask.FromResult(SyntheticCredentials.Token);

    private static void ScriptReplay(ShardHarness shard)
    {
        string Entry(string id, string at) => Searchsets.Entry(Searchsets.Patient(id, "1", at));
        string next = $"{shard.Search}?{Sort}&ct=A";
        shard.Server.Respond(
            ScriptedResponse.Json(Searchsets.Page($"{shard.Search}?{Sort}", next, null, Entry("a1", "2026-01-02T03:00:00Z"))),
            ScriptedResponse.Json(Searchsets.Page(null, next + "B", null, Entry("b1", "2026-01-02T03:30:00Z"))),
            ScriptedResponse.Json(Searchsets.Page(null, next + "C", null, Entry("b1", "2026-01-02T03:30:00Z"))));
    }

    [Theory]
    [MemberData(nameof(StrictStages))]
    public async Task StrictProbe_AnOutageAtAStage_IsThatStagesNegativeAndSendsNothingForIt(
        ShardSearchMethod method, int call, StrictHandlingProbeOutcome expected)
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        FailAcquisition(shard, call);

        StrictHandlingProbeResult result = await StrictHandlingProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, method, Ct);

        result.Outcome.ShouldBe(expected);
        result.IsProven.ShouldBeFalse();
        shard.Credentials.Calls.ShouldBe(call, "nothing is acquired or tried after the outage");
        shard.Server.Requests.Count.ShouldBe(call - 1, "the failed exchange itself is never sent");
        shard.Meter.Sends.Count.ShouldBe(call - 1);
    }

    [Theory]
    [MemberData(nameof(ReplayStages))]
    public async Task ReplayProbe_AnOutageAtAStage_IsReadFailedAndSendsNothingForIt(ShardSearchMethod method, int call)
    {
        await using var shard = ShardHarness.Start();
        ScriptReplay(shard);
        FailAcquisition(shard, call);

        LastUpdatedReplayProbeResult result = await LastUpdatedReplayProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, method, Ct);

        result.Outcome.ShouldBe(LastUpdatedReplayProbeOutcome.ReadFailed);
        result.IsObserved.ShouldBeFalse();
        shard.Credentials.Calls.ShouldBe(call, "nothing is acquired or tried after the outage");
        shard.Server.Requests.Count.ShouldBe(call - 1, "the failed exchange itself is never sent");
    }

    [Theory]
    [MemberData(nameof(CredentialFailures))]
    public async Task ProbeSearchAndRedemption_ReportACredentialFailureAsALocalSanitizedFailureAndSendNothing(ShardCredentialFailure cause)
    {
        await using var shard = ShardHarness.Start();
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(new ShardCredentialException(cause));

        ShardCallException search = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchForProbeAsync(shard.SarahRequest(), witness: null, Ct));
        ShardCallException redemption = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.ContinueForProbeAsync(shard.Cursor(shard.Search + "?ct=A"), Ct));

        foreach (ShardCallException failure in new[] { search, redemption })
        {
            failure.Failure.ShouldBe(ShardCallFailure.CredentialUnavailable);
            failure.Shard.ShouldBe(shard.Shard.Id);
            failure.StatusCode.ShouldBeNull();
            failure.Fault.ShouldBe(ShardFault.None, "an unsent local failure is not a wire fault");
            failure.InnerException.ShouldBeNull("the credential exception must not be chained into diagnostics");
            failure.Message.ShouldNotContain(cause.ToString());
            failure.Message.ShouldNotContain("secret", Case.Insensitive);
        }

        shard.Credentials.Calls.ShouldBe(2);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task StrictProbe_KeepsACallerCancellationAsCancellationEvenIfTheProviderReportsAnOutage()
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task cancellation = Task.CompletedTask;
        shard.Credentials.Acquire = _ =>
        {
            if (shard.Credentials.Calls != 2)
            {
                return ValueTask.FromResult(SyntheticCredentials.Token);
            }

            // Cancel the caller and fail in the same acquisition, so the outage cannot be told apart from the cancellation.
            cancellation = caller.CancelAsync();
            throw Outage();
        };

        OperationCanceledException cancelled = await Should.ThrowAsync<OperationCanceledException>(
            () => StrictHandlingProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, ShardSearchMethod.Get, caller.Token));
        await cancellation;

        cancelled.CancellationToken.ShouldBe(caller.Token);
        shard.Server.Requests.Count.ShouldBe(1, "only the metadata fetch was sent");
    }

    [Fact]
    public async Task StrictProbe_ACredentialExceptionFromTheMeterAfterTheSend_IsStillAFault()
    {
        await using var shard = ShardHarness.Start();
        ProbeScript.RespondWithChallenge(shard, ScriptedResponse.Status(400));
        ShardCredentialException fault = Outage();
        shard.Meter.ThrowOnSend = fault;
        shard.Meter.ThrowFromSend = 2; // the positive control; the metadata fetch before it is within budget

        ShardCredentialException thrown = await Should.ThrowAsync<ShardCredentialException>(
            () => StrictHandlingProbe.ProbeAsync(shard.Client, shard.Shard, ShardHarness.Patient, ShardSearchMethod.Get, Ct));

        thrown.ShouldBeSameAs(fault);
        shard.Credentials.Calls.ShouldBe(2, "the token was acquired, so the outage is not the provider's");
    }

    [Fact]
    public async Task OrdinaryContinueAndRead_KeepTheCredentialProvidersOwnException()
    {
        await using var shard = ShardHarness.Start();
        ShardCredentialException outage = Outage();
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(outage);

        (await Should.ThrowAsync<ShardCredentialException>(
            () => shard.Client.ContinueAsync(shard.Cursor(shard.Search + "?ct=A"), Ct))).ShouldBeSameAs(outage);
        (await Should.ThrowAsync<ShardCredentialException>(
            () => shard.Client.ReadAsync(new ResourceKey(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse("a1")), Ct))).ShouldBeSameAs(outage);

        shard.Server.Requests.ShouldBeEmpty();
    }
}

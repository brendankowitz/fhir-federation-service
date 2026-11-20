using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Diagnostics;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// One shard's <c>GET {base}/metadata</c>, observed at real TLS loopback servers (design spec §8, §9.1, §10.2): the same
/// credential, deadline, size and no-redirect rules as a search, with the body read as an R4 CapabilityStatement and
/// nothing else assumed from it. Pure reader cases live with <see cref="CapabilityStatementReader"/>.
/// </summary>
public sealed class HttpShardClientMetadataTests
{
    private const string SecretBody = "Sarah synthetic-token internal detail";

    private const string Statement =
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"instance\"," +
        "\"software\":{\"name\":\"Synthetic FHIR\",\"version\":\"9.1\"}," +
        "\"rest\":[{\"mode\":\"server\",\"resource\":[{\"type\":\"Patient\",\"interaction\":[{\"code\":\"search-type\"}]," +
        "\"searchParam\":[{\"name\":\"name\",\"type\":\"string\"}]}]}]}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, ShardCallFailure> RefusedStatuses => new()
    {
        { 404, ShardCallFailure.UnexpectedStatus },
        { 410, ShardCallFailure.UnexpectedStatus },
        { 401, ShardCallFailure.Authentication },
        { 403, ShardCallFailure.Authentication },
        { 400, ShardCallFailure.UnexpectedStatus },
        { 500, ShardCallFailure.UnexpectedStatus },
        { 201, ShardCallFailure.UnexpectedStatus },
        { 204, ShardCallFailure.UnexpectedStatus },
        { 302, ShardCallFailure.Redirect },
    };

    public static TheoryData<string> MalformedStatements => new()
    {
        string.Empty,
        "not json",
        "[]",
        "{\"resourceType\":\"Patient\",\"id\":\"p1\"}",
        "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"}",
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"5.0.0\",\"kind\":\"instance\"}",
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"requirements\"}",
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"instance\",\"kind\":\"instance\"}",
        "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"instance\",\"rest\":[{\"mode\":\"server\"},{\"mode\":\"server\"}]}",
    };

    [Fact]
    public async Task GetCapabilities_SendsOneAuthenticatedGetAtTheBaseAndReturnsTheAdvertisement()
    {
        await using var shard = ShardHarness.Start();
        shard.Credentials.Acquire = _ => ValueTask.FromResult("east-synthetic-token");
        shard.Server.Respond(ScriptedResponse.Json(Statement));

        ShardCapabilityEvidence evidence = await shard.Client.GetCapabilitiesAsync(Ct);

        RecordedRequest request = shard.Server.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("GET");
        request.Target.ShouldBe("/fhir/metadata");
        request.Body.ShouldBeEmpty();
        request.HeaderValues("Authorization").ShouldBe(["Bearer east-synthetic-token"]);
        request.HeaderValues("Accept").ShouldBe(["application/fhir+json"]);
        request.HeaderValues("Prefer").ShouldBe(["handling=strict"]);
        evidence.SearchableResources.Keys.ShouldBe([ShardHarness.Patient]);
        evidence.SearchableResources[ShardHarness.Patient].SearchParameters.Select(p => p.Code).ShouldBe(["name"]);
        evidence.Software.ShouldNotBeNull().Name.ShouldBe("Synthetic FHIR");
        evidence.Software.Version.ShouldBe("9.1");
        shard.Credentials.Calls.ShouldBe(1);
        shard.Meter.Sends.ShouldBe([shard.Shard.Id]);
        shard.Meter.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(Statement));
    }

    [Fact]
    public async Task GetCapabilities_KeepsTheRegisteredPrefixAndItsEscapedBytes()
    {
        await using var shard = ShardHarness.Start(basePath: "/tenant%2fa/fhir");
        shard.Server.Respond(ScriptedResponse.Json(Statement));

        await shard.Client.GetCapabilitiesAsync(Ct);

        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/tenant%2Fa/fhir/metadata");
    }

    [Fact]
    public async Task StatementWithoutSoftware_HasNoSoftwareEvidence()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json("{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.0\",\"kind\":\"capability\"}"));

        ShardCapabilityEvidence evidence = await shard.Client.GetCapabilitiesAsync(Ct);

        evidence.Software.ShouldBeNull();
        evidence.SearchableResources.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(MalformedStatements))]
    public async Task BodyThatIsNotAnAdvertisingR4Statement_IsMalformedAndLeaksNothing(string body)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(body));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        failure.StatusCode.ShouldBeNull();
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("CapabilityStatement at");
        failure.Message.ShouldNotContain("5.0.0");
        failure.Message.ShouldNotContain("requirements");
        failure.Message.ShouldNotContain("$");
        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidUtf8InAnIgnoredProperty_IsMalformedEvenThoughTheReaderWouldNotLookAtIt()
    {
        await using var shard = ShardHarness.Start();
        byte[] body = [.. "{\"resourceType\":\"CapabilityStatement\",\"fhirVersion\":\"4.0.1\",\"kind\":\"instance\",\"copyright\":\""u8, 0xFF, .. "\"}"u8];
        shard.Server.Respond(ScriptedResponse.Bytes(body));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        failure.Message.ShouldContain("UTF-8");
        failure.InnerException.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task NonOkStatus_IsClassifiedWithoutReadingItsBodyOrFollowingUp(int status, ShardCallFailure expected)
    {
        await using var shard = ShardHarness.Start();
        ScriptedResponse refusal = status == 302
            ? ScriptedResponse.Redirect(shard.Server.BaseUrl + "/metadata?moved=1")
            : ScriptedResponse.Status(status, SecretBody);
        shard.Server.Respond(refusal, ScriptedResponse.Json(Statement));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(expected);
        failure.StatusCode.ShouldBe(status);
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("search");
        failure.Message.ShouldNotContain("resource");
        failure.Message.ShouldNotContain("Sarah");
        failure.Message.ShouldNotContain(SyntheticCredentials.Token);
        failure.Message.ShouldNotContain("internal detail");
        failure.Message.ShouldNotContain("127.0.0.1");
        failure.Message.ShouldNotContain("moved");
        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(1);
        shard.Meter.Bytes.ShouldBe(0);
    }

    [Fact]
    public async Task ResponseOfUnknownLength_IsBoundedAtOneByteOverTheLimit()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        shard.Server.Respond(ScriptedResponse.Json(Statement + new string(' ', limit + 100 - Statement.Length)).WithoutContentLength());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Meter.Bytes.ShouldBe(limit + 1);
    }

    [Fact]
    public async Task OversizeAdvertisedLength_IsRefusedWithoutReadingTheBody()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        shard.Server.Respond(ScriptedResponse.Json(Statement + new string(' ', limit + 1 - Statement.Length)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Meter.Bytes.ShouldBe(0);
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Stall());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
    }

    [Fact]
    public async Task UntrustedCertificate_IsATransportFailureAndNoRequestIsRead()
    {
        await using var shard = ShardHarness.Start(trustLoopback: false);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("127.0.0.1");
        failure.Message.ShouldNotContain("/fhir");
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledCall_SendsNothing()
    {
        await using var shard = ShardHarness.Start();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.GetCapabilitiesAsync(caller.Token));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellingWhileTheResponseIsGated_PropagatesAsCancellationNotTimeout()
    {
        await using var shard = ShardHarness.Start();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shard.Server.Respond(ScriptedResponse.Json(Statement, beforeHead: async ct =>
        {
            arrived.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<ShardCapabilityEvidence> call = shard.Client.GetCapabilitiesAsync(caller.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await caller.CancelAsync();

        OperationCanceledException cancelled = await Should.ThrowAsync<OperationCanceledException>(() => call);
        cancelled.CancellationToken.ShouldBe(caller.Token);
        shard.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetCapabilities_UsesItsOwnMetadataSpanAndNamesAFailure()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(404, SecretBody));
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Ignixa.Federation",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = a =>
            {
                lock (stopped)
                {
                    stopped.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("synthetic-test-parent");
        parent.Start();

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.GetCapabilitiesAsync(Ct));

        Activity span;
        lock (stopped)
        {
            span = stopped.Single(a => a.TraceId == parent.TraceId);
        }

        span.OperationName.ShouldBe("shard metadata");
        span.GetTagItem(ShardActivity.StageTag).ShouldBe("metadata");
        span.GetTagItem(ShardActivity.ShardAliasTag).ShouldBe(ShardHarness.ShardAlias);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe(nameof(ShardCallFailure.UnexpectedStatus));
    }
}

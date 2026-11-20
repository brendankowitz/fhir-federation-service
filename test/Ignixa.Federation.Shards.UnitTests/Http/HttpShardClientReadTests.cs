using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Shards.Diagnostics;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A qualified current-resource read, observed at real TLS loopback servers (design spec §5.1, §7.8, §8, §10.2): it asks
/// the key's own shard, trusts only the shard's registry for the URL, and returns the body only when it is exactly the
/// requested resource.
/// </summary>
public sealed class HttpShardClientReadTests
{
    private const string SecretBody = "Sarah synthetic-token internal detail";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<int, ShardCallFailure> RefusedStatuses => new()
    {
        { 404, ShardCallFailure.ResourceNotFound },
        { 410, ShardCallFailure.ResourceGone },
        { 401, ShardCallFailure.Authentication },
        { 403, ShardCallFailure.Authentication },
        { 400, ShardCallFailure.UnexpectedStatus },
        { 500, ShardCallFailure.UnexpectedStatus },
        { 503, ShardCallFailure.UnexpectedStatus },
        { 201, ShardCallFailure.UnexpectedStatus },
        { 204, ShardCallFailure.UnexpectedStatus },
        { 302, ShardCallFailure.Redirect },
    };

    public static TheoryData<string> MalformedBodies => new()
    {
        string.Empty,
        "not json",
        "[]",
        "\"Patient\"",
        "{\"resourceType\":\"Patient\",\"id\":\"dup\"} {\"resourceType\":\"Patient\",\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"dup\"",
        "{\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\"}",
        "{\"resourceType\":\"Observation\",\"id\":\"dup\"}",
        "{\"resourceType\":\"patient\",\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"other\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"DUP\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"dup \"}",
        "{\"resourceType\":\"Patient\",\"id\":7}",
        "{\"resourceType\":\"Patient\",\"id\":null}",
        "{\"resourceType\":[\"Patient\"],\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"dup\",\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\",\"resourceType\":\"Patient\",\"id\":\"dup\"}",
        "{\"resourceType\":\"Patient\",\"id\":\"dup\",\"name\":[{\"family\":\"x\",\"family\":\"y\"}]}",
        "{\"resourceType\":\"Patient\",\"id\":\"d\\ud800p\"}",
        "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"entry\":[{\"resource\":{\"resourceType\":\"Patient\",\"id\":\"dup\"}}]}",
    };

    [Fact]
    public async Task CollidingIdsOnTwoShards_AreEachReadFromTheirOwnShardWithTheirOwnCredential()
    {
        await using var east = ShardHarness.Start(id: "east");
        await using var west = ShardHarness.Start(id: "west");
        ResourceKey eastKey = await FindDupAsync(east);
        ResourceKey westKey = await FindDupAsync(west);
        eastKey.Id.ShouldBe(westKey.Id);
        eastKey.ShouldNotBe(westKey);

        byte[] eastBody = Encoding.UTF8.GetBytes("{\"resourceType\":\"Patient\",\"id\":\"dup\",\"managingOrganization\":{\"reference\":\"Organization/1\"},\"note\":\"east\"}");
        byte[] westBody = Encoding.UTF8.GetBytes("{\"resourceType\":\"Patient\",\"id\":\"dup\",\"managingOrganization\":{\"reference\":\"Organization/1\"},\"note\":\"west\"}");
        east.Server.Respond(ScriptedResponse.Bytes(eastBody));
        west.Server.Respond(ScriptedResponse.Bytes(westBody));

        ShardReadResult fromEast = await east.Client.ReadAsync(eastKey, Ct);
        ShardReadResult fromWest = await west.Client.ReadAsync(westKey, Ct);

        fromEast.Resource.ToArray().ShouldBe(eastBody);
        fromWest.Resource.ToArray().ShouldBe(westBody);
        fromEast.ContentLocation.OriginalString.ShouldBe(east.Server.BaseUrl + "/Patient/dup");
        fromWest.ContentLocation.OriginalString.ShouldBe(west.Server.BaseUrl + "/Patient/dup");
        fromEast.ContentLocation.IsAbsoluteUri.ShouldBeTrue();
        east.Server.Requests.Select(r => $"{r.Method} {r.Target}").ShouldBe(["GET /fhir/Patient?name=Sarah&_count=10", "GET /fhir/Patient/dup"]);
        west.Server.Requests.Select(r => $"{r.Method} {r.Target}").ShouldBe(["GET /fhir/Patient?name=Sarah&_count=10", "GET /fhir/Patient/dup"]);
        RecordedRequest read = east.Server.Requests[1];
        read.Body.ShouldBeEmpty();
        read.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
        read.HeaderValues("Accept").ShouldBe(["application/fhir+json"]);
        east.Credentials.Calls.ShouldBe(2);
        west.Credentials.Calls.ShouldBe(2);
        east.Meter.Sends.Count.ShouldBe(2);
        east.Meter.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(east.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("dup")))) + eastBody.Length);
    }

    [Fact]
    public async Task ReadResult_IsTheExactResponseBytesWhateverTheBackendSaysAboutLocation()
    {
        await using var shard = ShardHarness.Start();
        ResourceKey key = Key(shard, "p1");
        byte[] body = Encoding.UTF8.GetBytes(
            "\n  {\r\n \"id\" : \"p1\" ,\t\"resourceType\":\"Patient\", \"note\":\"Zo\u00eb \u20ac \\u00e9 \ud83d\ude00\", \"link\":[{\"other\":{\"reference\":\"Patient/other\"}}] }  \n");
        shard.Server.Respond(ScriptedResponse.Json(Encoding.UTF8.GetString(body), contentLocation: "https://elsewhere.invalid/fhir/Patient/other/_history/9"));

        ShardReadResult result = await shard.Client.ReadAsync(key, Ct);

        result.Resource.ToArray().ShouldBe(body);
        result.ContentLocation.OriginalString.ShouldBe(shard.Server.BaseUrl + "/Patient/p1");
        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient/p1");
    }

    [Fact]
    public async Task ReadResult_DoesNotAliasTheClientsBuffers()
    {
        await using var shard = ShardHarness.Start();
        byte[] body = Encoding.UTF8.GetBytes("{\"resourceType\":\"Patient\",\"id\":\"p1\"}");
        shard.Server.Respond(ScriptedResponse.Bytes(body).WithoutContentLength(), ScriptedResponse.Bytes(Encoding.UTF8.GetBytes("{\"resourceType\":\"Patient\",\"id\":\"p2\"}")));

        ShardReadResult first = await shard.Client.ReadAsync(Key(shard, "p1"), Ct);
        await shard.Client.ReadAsync(Key(shard, "p2"), Ct);

        first.Resource.ToArray().ShouldBe(body);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("...")]
    [InlineData(".a")]
    [InlineData("a..")]
    [InlineData("A-1.z")]
    public async Task IdsThatMerelyContainDots_AreRequestedVerbatim(string id)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json($"{{\"resourceType\":\"Patient\",\"id\":\"{id}\"}}"));

        ShardReadResult result = await shard.Client.ReadAsync(Key(shard, id), Ct);

        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient/" + id);
        result.ContentLocation.OriginalString.ShouldBe(shard.Server.BaseUrl + "/Patient/" + id);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task DotOnlyId_IsRefusedBeforeAnyCredentialOrSend(string id)
    {
        await using var shard = ShardHarness.Start();

        Should.Throw<ArgumentException>(() => { _ = shard.Client.ReadAsync(Key(shard, id), Ct); });

        shard.Credentials.Calls.ShouldBe(0);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task KeyOfAnotherShard_IsRefusedBeforeAnyCredentialOrSend()
    {
        await using var east = ShardHarness.Start(id: "east");
        await using var west = ShardHarness.Start(id: "west");

        Should.Throw<ArgumentException>(() => { _ = east.Client.ReadAsync(Key(west, "dup"), Ct); });
        Should.Throw<ArgumentNullException>(() => { _ = east.Client.ReadAsync(null!, Ct); });

        east.Credentials.Calls.ShouldBe(0);
        east.Meter.Sends.ShouldBeEmpty();
        east.Server.Requests.ShouldBeEmpty();
        west.Server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(RefusedStatuses))]
    public async Task NonOkStatus_IsClassifiedWithoutReadingItsBodyOrRetrying(int status, ShardCallFailure expected)
    {
        await using var shard = ShardHarness.Start();
        ScriptedResponse refusal = status == 302
            ? ScriptedResponse.Redirect(shard.Server.BaseUrl + "/Patient/p1?moved=1")
            : ScriptedResponse.Status(status, SecretBody);
        shard.Server.Respond(refusal, ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        failure.Failure.ShouldBe(expected);
        failure.StatusCode.ShouldBe(status);
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("Sarah");
        failure.Message.ShouldNotContain(SyntheticCredentials.Token);
        failure.Message.ShouldNotContain("internal detail");
        failure.Message.ShouldNotContain("127.0.0.1");
        failure.Message.ShouldNotContain("p1");
        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(1);
        shard.Meter.Bytes.ShouldBe(0);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task SearchStatus404And410_RemainUnexpectedStatus(int status)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(status, SecretBody));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.UnexpectedStatus);
        failure.StatusCode.ShouldBe(status);
    }

    [Fact]
    public void OriginalFailureValues_AreUnchangedAndTheNewOnesAreAppended()
    {
        string[] names = Enum.GetNames<ShardCallFailure>();

        names.Take(10).ShouldBe(
        [
            "Unclassified", "Authentication", "UnexpectedStatus", "Redirect", "Timeout", "Transport", "ResponseTooLarge",
            "MalformedResponse", "SelfLinkMismatch", "InvalidNextLink",
        ]);
        names.Skip(10).ShouldBe(["ResourceNotFound", "ResourceGone"]);
        ((int)ShardCallFailure.InvalidNextLink).ShouldBe(9);
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task BodyThatIsNotExactlyTheRequestedResource_IsMalformedAndLeaksNothing(string body)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(body));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "dup"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        failure.StatusCode.ShouldBeNull();
        failure.InnerException.ShouldBeNull();
        failure.Message.ShouldNotContain("other");
        failure.Message.ShouldNotContain("Observation");
        failure.Message.ShouldNotContain("dup");
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x69, 0x64, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x7B, 0x22, 0x69, 0x64, 0x22, 0x3A, 0x22, 0xC3, 0x22, 0x7D })]
    [InlineData(new byte[] { 0x7B, 0x22, 0x69, 0x64, 0x22, 0x3A, 0x22, 0xED, 0xA0, 0x80, 0x22, 0x7D })]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x7D })]
    public async Task BodyThatIsNotValidUtf8OrStartsWithABom_IsMalformed(byte[] body)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Bytes(body));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "dup"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task Body_ExactlyAtTheLimit_IsReadAndOneByteOverIsTooLarge()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        string resource = "{\"resourceType\":\"Patient\",\"id\":\"p1\"}";
        shard.Server.Respond(
            ScriptedResponse.Json(resource + new string(' ', limit - resource.Length)).WithoutContentLength(),
            ScriptedResponse.Json(resource + new string(' ', limit + 1 - resource.Length)).WithoutContentLength());

        ShardReadResult atLimit = await shard.Client.ReadAsync(Key(shard, "p1"), Ct);
        ShardCallException over = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        atLimit.Resource.Length.ShouldBe(limit);
        over.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Meter.Bytes.ShouldBe(limit + limit + 1);
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Stall());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
    }

    [Fact]
    public async Task ServerThatStallsInTheBody_TimesOut()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}").StallingInBody());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
    }

    [Fact]
    public async Task CredentialThatNeverArrives_TimesOutWithoutSending()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Credentials.Acquire = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return SyntheticCredentials.Token;
        };

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AlreadyCancelledCall_SendsNothing()
    {
        await using var shard = ShardHarness.Start();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.ReadAsync(Key(shard, "p1"), caller.Token));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellingAsTheBodyIsRead_GetsNoResourceEvenWhenItIsValid()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        shard.Meter.OnBytes = _ => caller.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.ReadAsync(Key(shard, "p1"), caller.Token));
    }

    [Fact]
    public async Task CallerCancellationWhileWaiting_PropagatesAsCancellationNotTimeout()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Stall());
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.ReadAsync(Key(shard, "p1"), caller.Token));
    }

    [Fact]
    public async Task MalformedRead_NeverRetriesAndSendsOnce()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"other\"}"), ScriptedResponse.Json("{\"resourceType\":\"Patient\",\"id\":\"p1\"}"));

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        shard.Server.Requests.ShouldHaveSingleItem();
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Read_EndsItsOwnSpanAndNamesAFailure()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(410, SecretBody));
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

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.ReadAsync(Key(shard, "p1"), Ct));

        Activity span;
        lock (stopped)
        {
            span = stopped.Single(a => a.TraceId == parent.TraceId);
        }

        span.OperationName.ShouldBe("shard read");
        span.GetTagItem(ShardActivity.ShardAliasTag).ShouldBe(ShardHarness.ShardAlias);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe(nameof(ShardCallFailure.ResourceGone));
    }

    private static ResourceKey Key(ShardHarness shard, string id) => new(shard.Shard.Id, ShardHarness.Patient, ResourceId.Parse(id));

    // Recovers the key as a search reports it, so the read starts from the identity a real caller would hold.
    private static async Task<ResourceKey> FindDupAsync(ShardHarness shard)
    {
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("dup")))));
        Core.Shards.ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        return page.Entries.Single().Key;
    }
}

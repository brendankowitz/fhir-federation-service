using System.Diagnostics;
using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Diagnostics;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// Every failure throws a classified <see cref="ShardCallException"/> (or propagates cancellation), never an empty page,
/// and its message carries no token, body, query text or host (design spec §8, §14).
/// </summary>
public sealed class HttpShardClientFailureTests
{
    private const string SecretBody = "Sarah synthetic-token internal detail";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(401, ShardCallFailure.Authentication)]
    [InlineData(403, ShardCallFailure.Authentication)]
    [InlineData(404, ShardCallFailure.UnexpectedStatus)]
    [InlineData(400, ShardCallFailure.UnexpectedStatus)]
    [InlineData(500, ShardCallFailure.UnexpectedStatus)]
    [InlineData(503, ShardCallFailure.UnexpectedStatus)]
    [InlineData(201, ShardCallFailure.UnexpectedStatus)]
    [InlineData(202, ShardCallFailure.UnexpectedStatus)]
    public async Task NonOkStatus_IsClassifiedWithoutLeakingTheExchange(int status, ShardCallFailure expected)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(status, SecretBody));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(expected);
        failure.StatusCode.ShouldBe(status);
        failure.Shard.ShouldBe(shard.Shard.Id);
        failure.InnerException.ShouldBeNull();
        AssertNothingLeaked(failure);
    }

    [Fact]
    public async Task Redirect_IsAFailureAndIsNotFollowed()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Redirect(shard.Search + "?name=Sarah&_count=10&moved=1"), ScriptedResponse.Json(shard.SarahPage()));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Redirect);
        failure.StatusCode.ShouldBe(302);
        shard.Server.Requests.Count.ShouldBe(1);
        AssertNothingLeaked(failure);
    }

    [Fact]
    public async Task BodyExactlyAtTheLimit_IsRead()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        shard.Server.Respond(ScriptedResponse.Json(Padded(shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("p1"))), limit)));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.ShouldHaveSingleItem();
        shard.Meter.Bytes.ShouldBe(limit);
    }

    [Fact]
    public async Task DeclaredLengthOverTheLimit_FailsBeforeReadingTheBody()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        shard.Server.Respond(ScriptedResponse.Json(Padded(shard.SarahPage(), limit + 1)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Meter.Bytes.ShouldBe(0);
        shard.Meter.Sends.Count.ShouldBe(1);
    }

    [Fact]
    public async Task UndeclaredBodyOverTheLimit_FailsWhileReading()
    {
        const int limit = 4096;
        await using var shard = ShardHarness.Start(maxResponseBytes: limit);
        shard.Server.Respond(ScriptedResponse.Json(Padded(shard.SarahPage(), 64 * 1024)).WithoutContentLength());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.ResponseTooLarge);
        shard.Meter.Bytes.ShouldBeGreaterThan(limit);
        shard.Meter.Bytes.ShouldBeLessThan(64 * 1024);
    }

    [Fact]
    public async Task UndeclaredBodyWithinTheLimit_IsRead()
    {
        await using var shard = ShardHarness.Start();
        string body = shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("p1")));
        shard.Server.Respond(ScriptedResponse.Json(body).WithoutContentLength());

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        page.Entries.ShouldHaveSingleItem();
        shard.Meter.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task ServerThatNeverAnswers_TimesOut()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Stall());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        AssertNothingLeaked(failure);
    }

    [Fact]
    public async Task ServerThatStallsInTheBody_TimesOut()
    {
        await using var shard = ShardHarness.Start(callTimeout: TimeSpan.FromMilliseconds(300));
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()).StallingInBody());

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

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

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsCancellationNotTimeout()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Stall());
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.SearchAsync(shard.SarahRequest(), caller.Token));
    }

    [Fact]
    public async Task AlreadyCancelledCall_SendsNothing()
    {
        await using var shard = ShardHarness.Start();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.SearchAsync(shard.SarahRequest(), caller.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => shard.Client.ContinueAsync(shard.Cursor("?ct=Mg"), caller.Token));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task UntrustedCertificate_IsATransportFailureAndNoRequestIsRead()
    {
        await using var shard = ShardHarness.Start(trustLoopback: false);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.Transport);
        failure.InnerException.ShouldBeNull();
        shard.Server.Requests.ShouldBeEmpty();
        AssertNothingLeaked(failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("synthetic token")]
    [InlineData("synthetic-token\r\nX-Injected: 1")]
    [InlineData("synthetic=token")]
    [InlineData("Zo\u00eb")]
    public async Task TokenThatCannotBeABearerCredential_IsRefusedWithoutEchoingIt(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        await using var shard = ShardHarness.Start();
        shard.Credentials.Acquire = _ => ValueTask.FromResult(token);

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        if (token.Length > 0)
        {
            failure.Message.ShouldNotContain(token);
        }

        failure.Message.ShouldNotContain("synthetic");
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task CredentialProviderFailure_Propagates()
    {
        await using var shard = ShardHarness.Start();
        var unavailable = new InvalidOperationException("synthetic identity endpoint unavailable");
        shard.Credentials.Acquire = _ => ValueTask.FromException<string>(unavailable);

        (await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(unavailable);

        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Meter_SeesOneSendAndEveryBodyByte()
    {
        await using var shard = ShardHarness.Start();
        string body = shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("p1")));
        shard.Server.Respond(ScriptedResponse.Json(body));

        await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        shard.Meter.Sends.ShouldBe([shard.Shard.Id]);
        shard.Meter.Bytes.ShouldBe(Encoding.UTF8.GetByteCount(body));
        shard.Meter.Chunks.ShouldAllBe(c => c.Shard == shard.Shard.Id && c.Count > 0);
    }

    [Fact]
    public async Task Meter_CountsTheSendOfAFailedCall()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody));

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        shard.Meter.Sends.Count.ShouldBe(1);
        shard.Meter.Bytes.ShouldBe(0);
    }

    [Fact]
    public async Task MeterRefusingTheSend_StopsItAndPropagates()
    {
        await using var shard = ShardHarness.Start();
        var exhausted = new InvalidOperationException("synthetic budget exhausted");
        shard.Meter.ThrowOnSend = exhausted;

        (await Should.ThrowAsync<InvalidOperationException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct))).ShouldBeSameAs(exhausted);

        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConcurrentCalls_AreIndependent()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond([.. Enumerable.Range(0, 8).Select(_ => ScriptedResponse.Json(shard.SarahPage(entries: Searchsets.Entry(Searchsets.Patient("p1")))))]);

        ShardPage[] pages = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shard.Client.SearchAsync(shard.SarahRequest(), Ct)));

        pages.ShouldAllBe(p => p.Entries.Count == 1);
        shard.Server.Requests.Count.ShouldBe(8);
        shard.Meter.Sends.Count.ShouldBe(8);
    }

    [Fact]
    public async Task FailedCall_EndsItsSpanAsAnErrorNamedByTheFailure()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Status(503, SecretBody));
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

        await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        Activity span;
        lock (stopped)
        {
            span = stopped.Single(a => a.TraceId == parent.TraceId);
        }

        span.OperationName.ShouldBe("shard search");
        span.GetTagItem(ShardActivity.ShardAliasTag).ShouldBe(ShardHarness.ShardAlias);
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe(nameof(ShardCallFailure.UnexpectedStatus));
    }

    [Fact]
    public void Constructor_RejectsMissingDependenciesAndNonPositiveLimits()
    {
        ShardDescriptor shard = ShardRegistry.Create(
            [new Core.Configuration.ShardOptions
            {
                Id = "east",
                Alias = "k7q2",
                BaseUrl = "https://127.0.0.1:9/fhir",
                Profile = ShardHarness.Repost.Name,
                Auth = new Core.Configuration.AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.synthetic.example" },
            }],
            [ShardHarness.Repost]).Shards.Single();
        var credentials = new SyntheticCredentials();
        TimeSpan second = TimeSpan.FromSeconds(1);

        Should.Throw<ArgumentNullException>(() => new HttpShardClient(null!, credentials, second, 1));
        Should.Throw<ArgumentNullException>(() => new HttpShardClient(shard, null!, second, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpShardClient(shard, credentials, TimeSpan.Zero, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpShardClient(shard, credentials, TimeSpan.FromSeconds(-1), 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpShardClient(shard, credentials, Timeout.InfiniteTimeSpan, 1));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpShardClient(shard, credentials, second, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => new HttpShardClient(shard, credentials, second, (long)Array.MaxLength + 1));
        new HttpShardClient(shard, credentials, second, Array.MaxLength).Dispose();
    }

    private static string Padded(string json, int length) => json + new string(' ', length - Encoding.UTF8.GetByteCount(json));

    private static void AssertNothingLeaked(ShardCallException failure)
    {
        string text = failure.Message;
        text.ShouldContain(ShardHarness.ShardAlias);
        text.ShouldNotContain("Sarah");
        text.ShouldNotContain(SyntheticCredentials.Token);
        text.ShouldNotContain("internal detail");
        text.ShouldNotContain("127.0.0.1");
        text.ShouldNotContain("/fhir");
    }
}

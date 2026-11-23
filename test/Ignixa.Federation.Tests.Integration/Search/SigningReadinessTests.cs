using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Budgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

public sealed class SigningReadinessTests
{
    private const string SearchPath = "/Patient?name=Orchid";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ColdDiscovery_DoesNotBlockSearchOrHealth_AndRecovers(bool stallMetadata)
    {
        var gate = new Gate();
        using var idp = new SyntheticIdentityProvider();
        Uri stalled = stallMetadata ? SyntheticIdentityProvider.MetadataAddress : idp.JwksAddress;
        idp.BeforeResponse = (address, _, cancellationToken) =>
            address == stalled ? gate.HoldAsync(cancellationToken) : Task.CompletedTask;
        using var host = StartCold(idp);
        string token = host.Token();
        Task<SearchResult> first = host.GetAsync(SearchPath, token);
        try
        {
            await Within(gate.Entered.Task);

            (await HealthAsync(host, "live")).ShouldBe(HttpStatusCode.OK);
            (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
            Task<SearchResult>[] searches = [first, .. Enumerable.Range(0, 7).Select(_ => host.GetAsync(SearchPath, token))];
            foreach (SearchResult response in await Within(Task.WhenAll(searches)))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                response.IssueCode.ShouldBe("transient");
                response.Challenges.ShouldBeEmpty();
            }

            idp.Requests.Count(uri => uri == stalled).ShouldBe(1);
            host.Shards.Calls.ShouldBeEmpty();

            gate.Release.TrySetResult();
            await host.WaitForReadinessAsync();
            (await Within(host.GetAsync(SearchPath, token))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            gate.Release.TrySetResult();
            await Within(first);
        }
    }

    [Fact]
    public async Task UnknownKey_TriggersOneRefresh_AndRetiredKeyStopsWorking()
    {
        var time = new RefreshClock();
        var gate = new Gate();
        using var idp = new SyntheticIdentityProvider();
        using var host = SearchHost.Start(setup =>
        {
            setup.IdentityProvider = idp;
            setup.Time = time;
        });
        await Within(time.CooldownArmed.Task);
        string oldToken = host.Token();
        (await Within(host.GetAsync(SearchPath, oldToken))).StatusCode.ShouldBe(HttpStatusCode.OK);
        int callsBeforeRefresh = host.Shards.Calls.Count;

        idp.Rotate();
        idp.BeforeResponse = (address, _, cancellationToken) =>
            address == idp.JwksAddress ? gate.HoldAsync(cancellationToken) : Task.CompletedTask;
        time.Advance(TimeSpan.FromSeconds(30));
        string newToken = host.Token();
        try
        {
            Task<SearchResult>[] burst = [.. Enumerable.Range(0, 8).Select(_ => host.GetAsync(SearchPath, newToken))];
            foreach (SearchResult response in await Within(Task.WhenAll(burst)))
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                response.Challenges.ShouldBe(["Bearer"]);
            }

            await Within(gate.Entered.Task);
            host.Shards.Calls.Count.ShouldBe(callsBeforeRefresh);
            idp.Requests.Count(uri => uri == idp.JwksAddress).ShouldBe(2);
            (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.OK);
            (await Within(host.GetAsync(SearchPath, oldToken))).StatusCode.ShouldBe(HttpStatusCode.OK);

            gate.Release.TrySetResult();
            await UntilAcceptedAsync(host, newToken);
            int callsAfterRefresh = host.Shards.Calls.Count;

            (await Within(host.GetAsync(SearchPath, oldToken))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await Within(host.GetAsync(SearchPath, host.Token(new SyntheticTokenRequest
            {
                Issuer = SyntheticIdentityProvider.TrustedIssuer + "/",
                Roles = [SearchWorld.ReaderRole],
            })))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await Within(host.GetAsync(SearchPath, host.Token(new SyntheticTokenRequest
            {
                Audience = SyntheticIdentityProvider.TrustedAudience + "/",
                Roles = [SearchWorld.ReaderRole],
            })))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await Within(host.GetAsync(SearchPath, host.Token(new SyntheticTokenRequest
            {
                Roles = [SearchWorld.ObservationRole],
            })))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            host.Shards.Calls.Count.ShouldBe(callsAfterRefresh);
        }
    finally
    {
        gate.Release.TrySetResult();
    }
    }

    [Fact]
    public async Task Deadline_StartsBeforeNamedBearerEvent_AndDoesNotStageLateSuccess()
    {
        var time = new ManualTimeProvider();
        var gate = new Gate();
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.ConfigureJwtBearer = options => options.Events.OnTokenValidated =
                _ => gate.HoldAsync(CancellationToken.None);
        });
        Task<SearchResult> search = host.GetAsync(SearchPath);
        try
        {
            await Within(gate.Entered.Task);
            time.Advance(TimeSpan.FromSeconds(30));
            host.Shards.Calls.ShouldBeEmpty();
            gate.Release.TrySetResult();
            SearchResult result = await Within(search);
            result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
            result.IssueCode.ShouldBe("timeout");
            result.Challenges.ShouldBeEmpty();
            host.Shards.Calls.ShouldBeEmpty();
        }
            finally
            {
                gate.Release.TrySetResult();
            }
        }

    [Fact]
    public async Task Health_DoesNotAuthenticateAnOtherwiseValidBearer()
    {
        using var host = SearchHost.Start(setup => setup.ConfigureJwtBearer = options =>
            options.Events.OnTokenValidated = _ => throw new InvalidOperationException("Health must not run bearer events."));
        string token = host.Token();

        (await HealthAsync(host, "live", token)).ShouldBe(HttpStatusCode.OK);
        (await HealthAsync(host, "ready", token)).ShouldBe(HttpStatusCode.OK);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Readiness_StillAggregatesOtherReadyChecksWhenSearchIsConfigured()
    {
        using var host = SearchHost.Start(setup =>
        {
            setup.WaitForReadiness = false;
            setup.ConfigureServices = services =>
                services.AddHealthChecks().AddCheck("other-dependency", () => HealthCheckResult.Unhealthy(), tags: ["ready"]);
        });
        await UntilAcceptedAsync(host, host.Token(), HttpStatusCode.ServiceUnavailable);

        (await HealthAsync(host, "live")).ShouldBe(HttpStatusCode.OK);
        (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task SnapshotThatExpiresDuringAuthentication_FailsClosedBeforeShardWork()
    {
        var time = new RefreshClock();
        var authentication = new Gate();
        var refresh = new Gate();
        using var idp = new SyntheticIdentityProvider();
        using var host = SearchHost.Start(setup =>
        {
            setup.IdentityProvider = idp;
            setup.Time = time;
            setup.ConfigureJwtBearer = options => options.Events.OnTokenValidated =
                _ => authentication.HoldAsync(CancellationToken.None);
        });
        idp.BeforeResponse = (_, _, cancellationToken) => refresh.HoldAsync(cancellationToken);
        await Within(time.CooldownArmed.Task);
        time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        try
        {
            await Within(refresh.Entered.Task);
            Task<SearchResult> search = host.GetAsync(SearchPath);
            await Within(authentication.Entered.Task);
            (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.OK);
            time.Advance(TimeSpan.FromSeconds(1));
            authentication.Release.TrySetResult();

            (await Within(search)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await Within(host.GetAsync(SearchPath))).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            host.Shards.Calls.ShouldBeEmpty();
        }
        finally
        {
            refresh.Release.TrySetResult();
            authentication.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryBodyRead_IsCancelledByFetchDeadlineOrHostShutdown(bool stopHost)
    {
        var time = new ManualTimeProvider();
        var gate = new Gate();
        using var idp = new SyntheticIdentityProvider();
        idp.BeforeResponse = async (address, response, cancellationToken) =>
        {
            if (address != SyntheticIdentityProvider.MetadataAddress)
            {
                return;
            }

            using HttpContent original = response.Content;
            byte[] bytes = await original.ReadAsByteArrayAsync(cancellationToken);
            response.Content = new StreamContent(new GatedBody(bytes, gate));
        };
        using var host = StartCold(idp, time);
        try
        {
            Task<SearchResult> search = host.GetAsync(SearchPath);
            await Within(gate.Entered.Task);
            if (stopHost)
            {
                host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(10));
            }

            await Within(gate.Cancelled.Task);
            idp.Requests.ShouldBe([SyntheticIdentityProvider.MetadataAddress]);
            host.Shards.Calls.ShouldBeEmpty();
            if (!stopHost)
            {
                (await Within(search)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            }
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("http-jwks")]
    [InlineData("oversized")]
    [InlineData("late")]
    [InlineData("unusable-key")]
    public async Task InvalidDiscovery_DoesNotPublish_AndWorkerRetries(string failure)
    {
        var time = new RefreshClock();
        bool late = failure == "late";
        time.Inner.TimersEnabled = !late;
        var gate = new Gate();
        using var idp = new SyntheticIdentityProvider();
        Uri metadata = SyntheticIdentityProvider.MetadataAddress;
        Uri jwks = idp.JwksAddress;
        if (failure == "issuer")
        {
            idp.AdvertisedIssuer += "/";
        }
        else if (failure == "http-jwks")
        {
            idp.JwksAddress = new UriBuilder(jwks) { Scheme = "http", Port = -1 }.Uri;
        }

        Uri invalidDocument = failure == "unusable-key" ? jwks : metadata;
        int attempts = 0;
        idp.BeforeResponse = async (address, response, cancellationToken) =>
        {
            if (address != invalidDocument || Interlocked.Increment(ref attempts) != 1)
            {
                return;
            }

            if (failure == "oversized")
            {
                using HttpContent original = response.Content;
                string valid = await original.ReadAsStringAsync(cancellationToken);
                byte[] bytes = Encoding.UTF8.GetBytes(valid.PadRight((256 * 1024) + 1, ' '));
                response.Content = new StreamContent(new StreamingBody(bytes));
            }
            else if (failure == "unusable-key")
            {
                using HttpContent original = response.Content;
                string zeroModulus = new('A', 342);
                response.Content = new StringContent(
                    $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"invalid","n":"{{zeroModulus}}","e":"AQAB"}]}""",
                    Encoding.UTF8,
                    "application/json");
            }

            await gate.HoldAsync(cancellationToken);
        };
        using var host = StartCold(idp, time);
        try
        {
            await Within(gate.Entered.Task);
            (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
            idp.AdvertisedIssuer = SyntheticIdentityProvider.TrustedIssuer;
            idp.JwksAddress = jwks;
            if (late)
            {
                // Release the headers after the fetch deadline with the timer callback still undelivered.
                time.Advance(TimeSpan.FromSeconds(30));
                time.Inner.TimersEnabled = true;
            }

            gate.Release.TrySetResult();
            if (!late)
            {
                await Within(time.CooldownArmed.Task);
                (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
                time.Advance(TimeSpan.FromSeconds(30));
            }

            await host.WaitForReadinessAsync();

            Uri[] expected = failure == "unusable-key" ? [metadata, jwks, metadata, jwks] : [metadata, metadata, jwks];
            idp.Requests.ShouldBe(expected);
            (await Within(host.GetAsync(SearchPath))).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    private static SearchHost StartCold(SyntheticIdentityProvider idp, TimeProvider? time = null) =>
        SearchHost.Start(setup =>
        {
            setup.WaitForReadiness = false;
            setup.IdentityProvider = idp;
            setup.Time = time;
        });

    private static async Task<HttpStatusCode> HealthAsync(SearchHost host, string path, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/" + path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(Guard);
        using HttpResponseMessage response = await host.Client.SendAsync(request, guard.Token);
        return response.StatusCode;
    }

    private static async Task UntilAcceptedAsync(SearchHost host, string token, HttpStatusCode interimStatus = HttpStatusCode.Unauthorized)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(Guard);
        do
        {
            SearchResult result = await host.GetCancellableAsync(SearchPath, token, guard.Token);
            if (result.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            result.StatusCode.ShouldBe(interimStatus);
            await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token);
        }
        while (true);
    }

    private static Task Within(Task task) => task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    private static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HoldAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class GatedBody(byte[] bytes, Gate gate) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await gate.HoldAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class StreamingBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class RefreshClock : TimeProvider
    {
        public ManualTimeProvider Inner { get; } = new();
        public TaskCompletionSource CooldownArmed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long TimestampFrequency => Inner.TimestampFrequency;

        public override long GetTimestamp() => Inner.GetTimestamp();

        public void Advance(TimeSpan by) => Inner.Advance(by);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = Inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(30))
            {
                CooldownArmed.TrySetResult();
            }

            return timer;
        }
    }
}

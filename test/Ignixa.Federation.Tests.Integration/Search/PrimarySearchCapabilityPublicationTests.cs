using System.Net;
using System.Text.Json;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Reads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// A trusted host component publishes, replaces and withdraws the capabilities view of the real configured host through
/// <see cref="IFederationCapabilityPublisher"/>. Search, the capability statement, readiness and the owner-qualified read
/// all follow the one published view; a request in flight keeps the view it started with; a publication that cannot be
/// built changes nothing.
/// </summary>
public sealed class PrimarySearchCapabilityPublicationTests
{
    private const string PagedQuery = "/Patient?name=Orchid&_count=1";
    private const string GivenQuery = "/Patient?given=Orchid";
    private const string HeldSubject = "held-caller";
    private const string NotReady =
        """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"transient","diagnostics":"Federation capabilities are not ready."}]}""";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    /// <summary>The default view: <c>Patient</c> by <c>_id</c>, <c>family</c> and <c>name</c>.</summary>
    private static SearchCapabilities ViewA() => SearchWorld.Capabilities();

    /// <summary>
    /// A changed view that also searches <c>Patient.given</c>, built on a distinct registry object with the same version, so
    /// the configured shards still describe it.
    /// </summary>
    private static SearchCapabilities ViewB() => SearchWorld.Capabilities(patientGiven: true, registry: SearchWorld.ReverseRegistry(extraWitnessShard: false));

    /// <summary>A view certified against a three-shard registry, which the configured two shards do not describe.</summary>
    private static SearchCapabilities MismatchedView() => SearchWorld.Capabilities(registry: SearchWorld.ReverseRegistry(extraWitnessShard: true));

    [Fact]
    public async Task ColdHost_IsReadyOnPublication_FollowsEachView_AndNullWithdraws()
    {
        using SearchHost host = StartCold();
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        publisher.ShouldBeSameAs(host.Services.GetRequiredService<IFederationCapabilityPublisher>());
        await AssertColdAsync(host);

        publisher.Publish(ViewA());
        await host.WaitForReadinessAsync();
        await AssertWarmAsync(host, given: false);

        publisher.Publish(ViewB());
        await AssertWarmAsync(host, given: true);

        int calls = host.Shards.Calls.Count;
        publisher.Publish(null);
        await AssertColdAsync(host);
        host.Shards.Calls.Count.ShouldBe(calls);

        publisher.Publish(ViewA());
        await AssertWarmAsync(host, given: false);
    }

    [Fact]
    public async Task ContinuationIssuedUnderOneView_RedeemsUnderAnUnchangedProof_AndIsGoneOnceTheDefinitionIsRemoved()
    {
        using SearchHost host = StartCold();
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        publisher.Publish(ViewA());
        await host.WaitForReadinessAsync();
        string next = (await host.GetAsync(PagedQuery)).Link("next").ShouldNotBeNull();

        publisher.Publish(ViewB());
        SearchResult unchanged = await host.FollowAsync(next);
        unchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        unchanged.Entries.Single().FullUrl.ShouldBe("https://fhir-east.example.org/Patient/east-only");

        int calls = host.Shards.Calls.Count;
        publisher.Publish(null);
        SearchResult withdrawn = await host.FollowAsync(next);
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        withdrawn.Body.ShouldBe(NotReady);

        publisher.Publish(SearchWorld.Capabilities(patientName: false));
        SearchResult removed = await host.FollowAsync(next);
        removed.StatusCode.ShouldBe(HttpStatusCode.Gone);
        removed.Body.ShouldNotContain("Bundle");
        host.Shards.Calls.Count.ShouldBe(calls);

        publisher.Publish(ViewB());
        (await host.FollowAsync(next)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QualifiedRead_UsesTheRegistryOfTheViewPublishedAtThatRequest_AndNoReadHappensWhenCold()
    {
        var binding = new ScriptedReadBinding();
        using SearchHost host = StartCold(setup => setup.ConfigureServices = services => services.AddSingleton<IShardReadBinding>(binding));
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        SearchCapabilities a = ViewA();
        SearchCapabilities b = ViewB();
        b.Registry.ShouldNotBeSameAs(a.Registry);
        b.Registry.Version.ShouldBe(a.Registry.Version);

        (await Read(host)).Status.ShouldBe(503);
        binding.Calls.ShouldBeEmpty();

        publisher.Publish(a);
        await host.WaitForReadinessAsync();
        (await Read(host)).Status.ShouldBe(200);
        publisher.Publish(b);
        (await Read(host)).Status.ShouldBe(200);

        binding.Calls.Count.ShouldBe(2);
        binding.Calls[0].Registry.ShouldBeSameAs(a.Registry);
        binding.Calls[1].Registry.ShouldBeSameAs(b.Registry);

        publisher.Publish(null);
        (await Read(host)).Status.ShouldBe(503);
        binding.Calls.Count.ShouldBe(2);

        static Task<ReadResponse> Read(SearchHost host) => ReadResponse.GetAsync(host, "/shards/k7q2/Patient/dup", host.Token());
    }

    [Fact]
    public async Task Overlap_RequestInFlightKeepsItsViewWhileANewerViewIsPublished_AndANewRequestSeesTheNewer()
    {
        var gate = new Gate();
        using SearchHost host = StartCold(setup => setup.ConfigureJwtBearer = options => options.Events.OnTokenValidated = HoldOnly(gate));
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        SearchCapabilities b = ViewB();
        publisher.Publish(ViewA());
        await host.WaitForReadinessAsync();

        Task<SearchResult> held = host.GetAsync(GivenQuery, HeldToken(host));
        try
        {
            await Within(gate.Entered.Task);
            publisher.Publish(b);
            SearchResult fresh = await Within(host.GetAsync(GivenQuery));
            fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
            host.Shards.Views.Single().ShouldBeSameAs(b);

            gate.Release.TrySetResult();
            SearchResult old = await Within(held);
            old.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            old.IssueCode.ShouldBe("not-supported");
            host.Shards.Views.Count.ShouldBe(1);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Overlap_RequestInFlightCompletesOnItsViewAfterWithdrawal_AndANewRequestIsRefused()
    {
        var gate = new Gate();
        using SearchHost host = StartCold(setup => setup.ConfigureJwtBearer = options => options.Events.OnTokenValidated = HoldOnly(gate));
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        SearchCapabilities b = ViewB();
        publisher.Publish(b);
        await host.WaitForReadinessAsync();

        Task<SearchResult> held = host.GetAsync(GivenQuery, HeldToken(host));
        try
        {
            await Within(gate.Entered.Task);
            publisher.Publish(null);
            SearchResult fresh = await Within(host.GetAsync(GivenQuery));
            fresh.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            fresh.Body.ShouldBe(NotReady);
            host.Shards.Calls.ShouldBeEmpty();

            gate.Release.TrySetResult();
            (await Within(held)).StatusCode.ShouldBe(HttpStatusCode.OK);
            host.Shards.Views.Single().ShouldBeSameAs(b);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task WithdrawalIssuedWhileAViewIsBeingBuilt_IsNotOverwrittenByThatBuild_AndReadersDoNotWait()
    {
        var gate = new Gate();
        var time = new BuildBlockingTime();
        using SearchHost host = StartCold(setup => setup.Time = time);
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        publisher.Publish(ViewA());
        await host.WaitForReadinessAsync();
        SearchCapabilities b = ViewB();

        Task build = Task.Run(
            () =>
            {
                BuildBlockingTime.Blocked = gate;
                try
                {
                    publisher.Publish(b);
                }
                finally
                {
                    BuildBlockingTime.Blocked = null;
                }
            },
            TestContext.Current.CancellationToken);
        var withdrawStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task withdraw = Task.CompletedTask;
        try
        {
            await Within(gate.Entered.Task);
            (await SearchParametersAsync(host)).ShouldNotContain("given");
            (await Within(host.GetAsync(GivenQuery))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            withdraw = Task.Run(
                () =>
                {
                    withdrawStarted.SetResult();
                    publisher.Publish(null);
                },
                TestContext.Current.CancellationToken);
            await Within(withdrawStarted.Task);

            // Give an unserialized withdrawal the chance to finish before the build does.
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await Within(build);
        await Within(withdraw);
        await AssertColdAsync(host);
    }

    [Fact]
    public async Task PublicationThatCannotBeBuilt_ThrowsAndChangesNothing_WhetherColdOrWarm()
    {
        using SearchHost host = StartCold();
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();

        Should.Throw<InvalidOperationException>(() => publisher.Publish(MismatchedView())).Message.ShouldContain("Federation:Shards");
        await AssertColdAsync(host);

        publisher.Publish(ViewA());
        await host.WaitForReadinessAsync();
        Should.Throw<InvalidOperationException>(() => publisher.Publish(MismatchedView())).Message.ShouldContain("Federation:Shards");
        await AssertWarmAsync(host, given: false);
    }

    [Fact]
    public async Task PublicationWithoutATransport_ThrowsAndLeavesTheHostCold()
    {
        using SearchHost host = StartCold(setup => setup.RegisterTransport = false);
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();

        Should.Throw<InvalidOperationException>(() => publisher.Publish(ViewA())).Message.ShouldContain("IShardClientFactory");

        await AssertColdAsync(host);
    }

    [Fact]
    public void RegisteredViewThatCannotBeBuilt_StillFailsStartup()
    {
        Should.Throw<Exception>(() =>
        {
            using SearchHost host = SearchHost.Start(setup => setup.Capabilities = MismatchedView());
        });
    }

    [Fact]
    public async Task RegisteredView_SeedsTheHostWarm_AndNullWithdrawsItWithoutFallingBack()
    {
        using SearchHost host = SearchHost.Start();
        IFederationCapabilityPublisher publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        await AssertWarmAsync(host, given: false);

        publisher.Publish(null);
        await AssertColdAsync(host);

        publisher.Publish(ViewB());
        await AssertWarmAsync(host, given: true);
    }

    [Fact]
    public void UnconfiguredHost_HasNoPublisher()
    {
        using SearchHost host = SearchHost.Start(setup => setup.Unconfigure());

        host.Services.GetService<IFederationCapabilityPublisher>().ShouldBeNull();
    }

    private static SearchHost StartCold(Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = null;
            setup.WaitForReadiness = false;
            configure?.Invoke(setup);
        });

    private static string HeldToken(SearchHost host) =>
        host.Token(new SyntheticTokenRequest { Subject = HeldSubject, Roles = [SearchWorld.ReaderRole], ObjectId = SearchWorld.CallerObjectId });

    /// <summary>Holds only the authentication of <see cref="HeldSubject"/>, which is after the request captured its view and before planning.</summary>
    private static Func<Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext, Task> HoldOnly(Gate gate) =>
        context => context.Principal?.FindFirst("sub")?.Value == HeldSubject ? gate.HoldAsync() : Task.CompletedTask;

    private static async Task AssertColdAsync(SearchHost host)
    {
        SearchResult[] refusals =
        [
            await host.GetAsync("/metadata", token: null),
            await host.GetAsync("/Patient?name=Orchid", host.Token()),
            await host.PostFormAsync("/Patient/_search", "name=Orchid"),
            await host.GetAsync("/Patient?ct=x", host.Token()),
            await host.GetAsync("/shards/k7q2/Patient/dup", host.Token()),
        ];
        foreach (SearchResult refusal in refusals)
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            refusal.Body.ShouldBe(NotReady);
        }

        (await HealthAsync(host, "live")).ShouldBe(HttpStatusCode.OK);
        (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(r => r.Name == PrimarySearchState.HealthCheckName, TestContext.Current.CancellationToken);
        report.Status.ShouldBe(HealthStatus.Unhealthy);
    }

    /// <summary>Readiness, the capability statement and both search forms agree on the view; <c>given</c> is the one parameter views A and B differ in.</summary>
    private static async Task AssertWarmAsync(SearchHost host, bool given)
    {
        (await HealthAsync(host, "ready")).ShouldBe(HttpStatusCode.OK);
        string[] expected = given ? ["_id", "family", "given", "name"] : ["_id", "family", "name"];
        (await SearchParametersAsync(host)).ShouldBe(expected);

        SearchResult get = await host.GetAsync("/Patient?name=Orchid");
        SearchResult post = await host.PostFormAsync("/Patient/_search", "name=Orchid");
        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        post.StatusCode.ShouldBe(HttpStatusCode.OK);
        post.Entries.Select(e => e.FullUrl).ShouldBe(get.Entries.Select(e => e.FullUrl));

        SearchResult byGiven = await host.GetAsync(GivenQuery);
        byGiven.StatusCode.ShouldBe(given ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
        SearchResult postGiven = await host.PostFormAsync("/Patient/_search", "given=Orchid");
        postGiven.StatusCode.ShouldBe(given ? HttpStatusCode.OK : HttpStatusCode.BadRequest);
    }

    private static async Task<string[]> SearchParametersAsync(SearchHost host)
    {
        ReadResponse metadata = await ReadResponse.GetAsync(host, "/metadata", token: null);
        metadata.Status.ShouldBe(200);
        JsonElement patient = JsonElement.Parse(metadata.Body).GetProperty("rest")[0].GetProperty("resource").EnumerateArray()
            .Single(r => r.GetProperty("type").GetString() == "Patient");
        return [.. patient.GetProperty("searchParam").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).Order(StringComparer.Ordinal)];
    }

    private static async Task<HttpStatusCode> HealthAsync(SearchHost host, string kind)
    {
        using HttpResponseMessage response = await host.Client.GetAsync(new Uri($"/health/{kind}", UriKind.Relative), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static Task Within(Task task) => task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    private static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Guard, TestContext.Current.CancellationToken);

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HoldAsync()
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(Guard);
        }

        /// <summary>Blocks the calling thread, for a code path that is synchronous.</summary>
        public void Hold()
        {
            Entered.TrySetResult();
            Release.Task.Wait(Guard);
        }
    }

    /// <summary>A clock that stalls one marked thread while it builds a view, so a build can be held in progress.</summary>
    private sealed class BuildBlockingTime : TimeProvider
    {
        [ThreadStatic]
        private static Gate? s_blocked;

        public static Gate? Blocked
        {
            get => s_blocked;
            set => s_blocked = value;
        }

        public override DateTimeOffset GetUtcNow()
        {
            s_blocked?.Hold();
            return base.GetUtcNow();
        }
    }
}

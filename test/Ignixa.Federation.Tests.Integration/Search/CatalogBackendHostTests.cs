using System.Net;
using System.Text;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// The ordinary <c>Program</c> activates proof-backed publication only for an explicit
/// <c>Federation:Backend:Mode=PrototypeCatalog</c>: then its state is genuinely cold, one hosted worker is the only
/// publisher, and a missing host section, invalid mode or missing source fails startup before any shard is contacted.
/// Without the setting every earlier path is unchanged, even when a source happens to be registered.
/// </summary>
public sealed class CatalogBackendHostTests
{
    private const string ModeKey = "Federation:Backend:Mode";
    private const string PrototypeCatalog = "PrototypeCatalog";
    private const string Query = "/Patient?name=Orchid";
    private const string NotReady =
        """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"transient","diagnostics":"Federation capabilities are not ready."}]}""";

    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModeAbsent_WithASourceRegistered_KeepsTheSeededPath_AndNeverRunsTheSource(bool hasOtherBackendSetting)
    {
        var source = new CapabilityPublicationWorkerTests.ScriptedAdmissions();
        using SearchHost host = SearchHost.Start(setup =>
        {
            if (hasOtherBackendSetting)
            {
                setup.Settings["Federation:Backend:Other"] = "unused";
            }

            setup.ConfigureServices = services => services.AddSingleton<ICapabilityAdmissionSource>(source);
        });

        (await host.GetAsync(Query)).StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Services.GetService<IFederationCapabilityPublisher>().ShouldNotBeNull();
        Workers(host).ShouldBeEmpty();
        source.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ModeAbsent_ConfiguredColdHost_StaysColdUntilAManualPublication_WithASourceRegistered()
    {
        var source = new CapabilityPublicationWorkerTests.ScriptedAdmissions();
        using SearchHost host = SearchHost.Start(setup =>
        {
            setup.Capabilities = null;
            setup.WaitForReadiness = false;
            setup.ConfigureServices = services => services.AddSingleton<ICapabilityAdmissionSource>(source);
        });

        SearchResult cold = await host.GetAsync(Query);
        cold.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        cold.Body.ShouldBe(NotReady);

        host.Services.GetRequiredService<IFederationCapabilityPublisher>().Publish(SearchWorld.Capabilities());
        (await host.GetAsync(Query)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Workers(host).ShouldBeEmpty();
        source.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ModeAbsent_UnconfiguredHost_IsStillAnUnconfiguredHost_WithASourceRegistered()
    {
        var source = new CapabilityPublicationWorkerTests.ScriptedAdmissions();
        using SearchHost host = SearchHost.Start(setup =>
        {
            setup.Unconfigure();
            setup.Capabilities = null;
            setup.WaitForReadiness = false;
            setup.ConfigureServices = services => services.AddSingleton<ICapabilityAdmissionSource>(source);
        });

        (await host.GetAsync("/health/live", token: null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.GetAsync(Query, token: null)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        host.Services.GetService<IFederationCapabilityPublisher>().ShouldBeNull();
        Workers(host).ShouldBeEmpty();
        source.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CatalogMode_StartsColdAndNeverResolvesTheSeed_UntilTheWorkerAdmits_ThenWithdrawsAndReadmits()
    {
        var source = new CapabilityPublicationWorkerTests.ScriptedAdmissions();
        var evidence = new CapabilityPublicationWorkerTests.ProofEvidence();
        var clock = new CapabilityPublicationWorkerTests.StepClock(due => due == OneSecond);
        int seedResolutions = 0;
        using SearchHost host = SearchHost.Start(setup =>
        {
            setup.Settings[ModeKey] = PrototypeCatalog;
            setup.Capabilities = null;
            setup.WaitForReadiness = false;
            setup.Time = clock;
            setup.ConfigureServices = services =>
            {
                services.AddSingleton<ICapabilityAdmissionSource>(source);
                services.AddSingleton(_ =>
                {
                    Interlocked.Increment(ref seedResolutions);
                    return SearchWorld.Capabilities();
                });
            };
        });

        try
        {
            // Cold while the first probe is pending, with the registered seed never resolved and no shard contacted.
            CapabilityPublicationWorkerTests.Probe first = await source.NextAsync();
            await AssertColdAsync(host);
            seedResolutions.ShouldBe(0);

            // An admission warms the host; the worker then waits its fixed one-second cadence on the injected clock.
            first.Release(evidence.Admit(SearchWorld.Capabilities()));
            CapabilityPublicationWorkerTests.StepTimer wait = await clock.NextWaitAsync();
            wait.Due.ShouldBe(OneSecond);
            await host.WaitForReadinessAsync();
            (await host.GetAsync(Query)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await host.GetAsync("/metadata", token: null)).StatusCode.ShouldBe(HttpStatusCode.OK);
            evidence.Commits.ShouldBe(1);

            // Replaced evidence is cold for every request at once; the worker then withdraws and probes again.
            evidence.Replace();
            await AssertColdAsync(host);
            wait.Fire();
            CapabilityPublicationWorkerTests.Probe second = await source.NextAsync();
            host.Services.GetRequiredService<PrimarySearchState>().Current.ShouldBeNull();
            await AssertColdAsync(host);

            // A refusal is paced by the same cadence, and a fresh admission recovers.
            second.Release(null);
            wait = await clock.NextWaitAsync();
            wait.Due.ShouldBe(OneSecond);
            await AssertColdAsync(host);
            wait.Fire();
            (await source.NextAsync()).Release(evidence.Admit(SearchWorld.Capabilities()));
            await clock.NextWaitAsync();
            await host.WaitForReadinessAsync();
            (await host.GetAsync(Query)).StatusCode.ShouldBe(HttpStatusCode.OK);

            seedResolutions.ShouldBe(0);
            source.Calls.ShouldBe(3);
            evidence.Commits.ShouldBe(2);
            host.Services.GetService<IFederationCapabilityPublisher>().ShouldBeNull();
            Workers(host).Count.ShouldBe(1);
        }
        finally
        {
            source.Close();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("prototypecatalog")]
    [InlineData("PrototypeCatalog ")]
    [InlineData("Static")]
    public void CatalogMode_WithABlankOrInvalidMode_FailsStartupBeforeAnyShardIsContacted(string mode) =>
        AssertFailsStartup(ModeKey, mode, "Federation:Backend:Mode", setup => { });

    [Fact]
    public void CatalogMode_WithAnObject_FailsStartupBeforeAnyShardIsContacted() =>
        AssertFailsStartup("Federation:Backend:Mode:Kind", PrototypeCatalog, ModeKey, setup => { });

    [Fact]
    public void CatalogMode_FailureNamesTheSettingAndNeverEchoesAnInvalidValue()
    {
        const string Hostile = "Hostile-ModeValue-91";

        string text = AssertFailsStartup(ModeKey, Hostile, "Federation:Backend:Mode", setup => { });

        text.ShouldNotContain(Hostile);
    }

    [Fact]
    public void CatalogMode_WithoutTheHostSection_FailsStartupNamingIt()
    {
        string text = AssertFailsStartup(ModeKey, PrototypeCatalog, "Federation:Host", setup => setup.Unconfigure());

        text.ShouldContain("Federation:Backend:Mode");
    }

    [Fact]
    public void CatalogMode_WithAnInvalidHostSection_KeepsItsOwnValidation()
    {
        string text = AssertFailsStartup(ModeKey, PrototypeCatalog, "Federation:Host:Authority", setup => setup.Settings["Federation:Host:Authority"] = "http://not-https.example.test");

        text.ShouldNotContain("http://not-https.example.test");
    }

    [Fact]
    public async Task CatalogMode_WithoutASource_FailsStartupNamingTheSetting_EvenWithASeedRegistered()
    {
        var shards = new ScriptedShards();
        WebApplicationBuilder builder = CreateCatalogBuilder();
        builder.Services.AddSingleton(SearchWorld.Capabilities());
        builder.Services.AddSingleton<IShardClientFactory>(shards);
        await using WebApplication host = builder.Build();

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            host.StartAsync(TestContext.Current.CancellationToken));

        failure.Message.ShouldContain(ModeKey);
        failure.Message.ShouldContain(nameof(ICapabilityAdmissionSource));
        shards.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task CatalogMode_DoesNotMaskAFailureOfASuppliedSource()
    {
        WebApplicationBuilder builder = CreateCatalogBuilder();
        var sourceFailure = new InvalidOperationException("source-construction-defect");
        builder.Services.AddSingleton<ICapabilityAdmissionSource>(_ => throw sourceFailure);
        await using WebApplication host = builder.Build();

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            host.StartAsync(TestContext.Current.CancellationToken));

        failure.ShouldBeSameAs(sourceFailure);
    }

    [Theory]
    [InlineData("""{"Federation":{"Backend":{"Mode":null}}}""")]
    [InlineData("""{"Federation":{"Backend":{"Mode":["PrototypeCatalog"]}}}""")]
    public void Selection_ExplicitNullOrArray_IsInvalid(string json)
    {
        InvalidOperationException failure = Should.Throw<InvalidOperationException>(() => CatalogBackendSelection.IsSelected(Json(json)));

        failure.Message.ShouldContain(ModeKey);
    }

    private static string AssertFailsStartup(string key, string? value, string expectedPath, Action<SearchHostSetup> adjust)
    {
        var shards = new ScriptedShards();
        var source = new CapabilityPublicationWorkerTests.ScriptedAdmissions();
        Exception failure = Should.Throw<Exception>(() =>
        {
            using SearchHost host = SearchHost.Start(setup =>
            {
                setup.Shards = shards;
                setup.Settings[key] = value;
                setup.WaitForReadiness = false;
                setup.ConfigureServices = services => services.AddSingleton<ICapabilityAdmissionSource>(source);
                adjust(setup);
            });
        });

        string text = failure.ToString();
        text.ShouldContain(expectedPath);
        shards.Calls.Count.ShouldBe(0);
        source.Calls.ShouldBe(0);
        return text;
    }

    // Own StartAsync and disposal separately so the startup exception is not racing Program.Run's host disposal.
    private static WebApplicationBuilder CreateCatalogBuilder()
    {
        var setup = new SearchHostSetup();
        setup.Settings[ModeKey] = PrototypeCatalog;
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(setup.Settings);
        return builder.AddPrimarySearch();
    }

    private static async Task AssertColdAsync(SearchHost host)
    {
        int shardCalls = host.Shards.Calls.Count;
        (await host.GetAsync("/health/live", token: null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await host.GetAsync("/health/ready", token: null)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        SearchResult metadata = await host.GetAsync("/metadata", token: null);
        metadata.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        metadata.Body.ShouldBe(NotReady);
        SearchResult search = await host.GetAsync(Query);
        search.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        search.Body.ShouldBe(NotReady);
        host.Shards.Calls.Count.ShouldBe(shardCalls);
    }

    private static List<CapabilityPublicationWorker> Workers(SearchHost host) =>
        [.. host.Services.GetServices<IHostedService>().OfType<CapabilityPublicationWorker>()];

    private static IConfiguration Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
}

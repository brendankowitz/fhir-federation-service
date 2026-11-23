using System.Net;
using System.Net.Sockets;
using Ignixa.Federation.Api.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// A host with valid <c>Federation:Host</c> settings and no capabilities view yet is a running process that is not ready:
/// every route that needs the view refuses with one fixed transient outcome before any budget, authentication, body or
/// shard work. The shards are real loopback listeners that must never be contacted; no view, transport, read binding or
/// count dialect is registered.
/// </summary>
public sealed class PrimarySearchColdHostTests
{
    private const string NotReady =
        """{"resourceType":"OperationOutcome","issue":[{"severity":"error","code":"transient","diagnostics":"Federation capabilities are not ready."}]}""";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ConfiguredHostWithoutAView_StaysUpNotReadyAndRefusesEveryCapabilityRoute()
    {
        using var east = new LoopbackShard();
        using var west = new LoopbackShard();
        using SearchHost host = SearchHost.Start(setup =>
        {
            setup.Capabilities = null;
            setup.RegisterTransport = false;
            setup.WaitForReadiness = false;
            setup.Settings["Federation:Shards:0:BaseUrl"] = east.BaseUrl;
            setup.Settings["Federation:Shards:1:BaseUrl"] = west.BaseUrl;
        });

        // Before the signing keys are warm, the missing view still wins: the refusal comes first.
        await AssertEveryCapabilityRouteIsRefusedAsync(host);

        await WaitForSigningAsync(host);
        HealthReport report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Tags.Contains("ready"), TestContext.Current.CancellationToken);
        report.Entries.Where(e => e.Value.Status == HealthStatus.Unhealthy).Select(e => e.Key).ShouldBe(["search-capabilities"]);

        SearchResult live = await host.GetAsync("/health/live", token: null);
        SearchResult ready = await host.GetAsync("/health/ready", token: null);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        live.Body.ShouldBe("Healthy");
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        ready.Body.ShouldBe("Unhealthy");

        await AssertEveryCapabilityRouteIsRefusedAsync(host);

        SearchResult unqualified = await host.GetAsync("/Patient/dup", token: null);
        unqualified.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unqualified.IssueCode.ShouldBe("invalid");
        unqualified.Body.ShouldNotContain("/shards/");

        east.Connections.ShouldBe(0);
        west.Connections.ShouldBe(0);
    }

    [Fact]
    public void ConfiguredHostWithAViewButInvalidOptions_FailsStartup()
    {
        Should.Throw<Exception>(() =>
        {
            using SearchHost host = SearchHost.Start(setup => setup.Settings["Federation:Limits:MaxPageSize"] = "101");
        });
    }

    private static async Task AssertEveryCapabilityRouteIsRefusedAsync(SearchHost host)
    {
        SearchResult[] refusals =
        [
            await host.GetAsync("/metadata", token: null),
            await host.GetAsync("/metadata?_format=xml", token: null),
            await host.GetAsync("/Patient?name=Orchid", host.Token()),
            await host.GetAsync("/Patient?name=Orchid", token: null),
            await host.PostFormAsync("/Patient/_search", "name=Orchid"),
            await host.GetAsync("/Patient?ct=x", host.Token()),
            await host.GetAsync("/shards/k7q2/Patient/dup", host.Token()),
            await host.GetAsync("/shards/k7q2/Patient/dup", token: null),
        ];

        foreach (SearchResult refusal in refusals)
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            refusal.MediaType.ShouldBe("application/fhir+json");
            refusal.Charset.ShouldBe("utf-8");
            refusal.Challenges.ShouldBeEmpty();
            refusal.Body.ShouldBe(NotReady);
        }
    }

    private static async Task WaitForSigningAsync(SearchHost host)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(Guard);
        HealthCheckService health = host.Services.GetRequiredService<HealthCheckService>();
        while (true)
        {
            HealthReport report = await health.CheckHealthAsync(
                registration => registration.Name == SigningConfiguration.HealthCheckName, guard.Token);
            if (report.Status == HealthStatus.Healthy)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), guard.Token);
        }
    }

    /// <summary>A loopback listener standing in for a shard; it counts the connections it accepts and never answers.</summary>
    private sealed class LoopbackShard : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private int _connections;

        public LoopbackShard()
        {
            _listener.Start();
            _accepting = AcceptAsync();
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public int Connections => Volatile.Read(ref _connections);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            _accepting.GetAwaiter().GetResult();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _connections);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal stops the listener.
            }
        }
    }
}

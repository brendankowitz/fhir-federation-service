using System.Net;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Budgets;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>Exercises count-only admission, fan-out and response shaping through the authenticated HTTP routes.</summary>
public sealed class PrimarySearchCountTests
{
    [Theory]
    [InlineData(false, "_count=0", "Orchid", 2)]
    [InlineData(false, "_summary=count", "Absent", 0)]
    [InlineData(true, "_summary=count", "Orchid", 2)]
    [InlineData(true, "_count=0", "Absent", 0)]
    public async Task CountOnly_UsesEachShardDialectAndReturnsOnlyTheExactTotal(
        bool post, string control, string name, int expectedTotal)
    {
        using var host = StartCountHost();

        SearchResult result = await CountAsync(host, post, $"name={name}&{control}");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.MediaType.ShouldBe("application/fhir+json");
        result.Json.GetProperty("resourceType").GetString().ShouldBe("Bundle");
        result.Json.GetProperty("type").GetString().ShouldBe("searchset");
        result.Json.GetProperty("total").GetInt32().ShouldBe(expectedTotal);
        result.Json.TryGetProperty("entry", out _).ShouldBeFalse();
        result.Json.GetProperty("link").GetArrayLength().ShouldBe(1);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name={name}&{control}");
        host.Shards.Calls.Count.ShouldBe(2);
        host.Shards.Calls.ShouldAllBe(c => c.PageSize == 0 && c.Target == null);
        ShardCall east = host.Shards.Calls.Single(c => c.Shard == "east");
        east.Method.ShouldBe(post ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get);
        east.Parameters.ShouldBe($"name={name}&_summary=count");
        ShardCall west = host.Shards.Calls.Single(c => c.Shard == "west");
        west.Method.ShouldBe(ShardSearchMethod.Get);
        west.Parameters.ShouldBe($"name={name}");
    }

    [Theory]
    [InlineData(false, "proof")]
    [InlineData(true, "dialect")]
    [InlineData(true, "no-map")]
    [InlineData(false, "undefined-dialect")]
    public async Task MissingOrInvalidCountSupport_RefusesBeforeAnyShardCall(bool post, string missing)
    {
        Dictionary<ShardId, CountDialect> dialects = Dialects();
        if (missing == "dialect")
        {
            dialects.Remove(ShardId.Parse("west"));
        }
        else if (missing == "undefined-dialect")
        {
            dialects[ShardId.Parse("west")] = (CountDialect)int.MaxValue;
        }

        using var host = StartCountHost(setup =>
        {
            setup.Capabilities = CertifiedView(missing == "proof" ? "west" : null);
            setup.ConfigureServices = missing == "no-map" ? null
                : services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(dialects);
        });

        SearchResult result = await CountAsync(host, post, "name=Orchid&_count=0");

        AssertFailure(result, HttpStatusCode.BadRequest, "not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("outage", HttpStatusCode.BadGateway, "exception")]
    [InlineData("no-total", HttpStatusCode.BadGateway, "exception")]
    [InlineData("overflow", HttpStatusCode.BadGateway, "exception")]
    [InlineData("bytes", HttpStatusCode.BadRequest, "too-costly")]
    [InlineData("sends", HttpStatusCode.BadRequest, "too-costly")]
    [InlineData("deadline", HttpStatusCode.GatewayTimeout, "timeout")]
    public async Task FailedCount_ReturnsAnOutcomeWithoutATotal(
        string failure, HttpStatusCode status, string issue)
    {
        var time = new ManualTimeProvider { TimersEnabled = false };
        using var host = StartCountHost(setup =>
        {
            setup.Settings["Federation:Limits:MaxParallelBackendCalls"] = "1";
            switch (failure)
            {
                case "outage":
                    setup.Shards.FailingShard = "west";
                    break;
                case "no-total":
                    setup.Shards.CountAnswer = request => new ShardPage([], null, request.Shard.Value == "east" ? 1 : null);
                    break;
                case "overflow":
                    setup.Shards.CountAnswer = _ => new ShardPage([], null, int.MaxValue);
                    break;
                case "bytes":
                    setup.Settings["Federation:Limits:MaxResponseBytes"] = "1";
                    setup.Shards.Answered = budget => budget.OnResponseBytesConsumed(2);
                    break;
                case "sends":
                    setup.Settings["Federation:Limits:BackendCallsPerShard"] = "1";
                    setup.Shards.SendsPerCall = 2;
                    break;
                case "deadline":
                    setup.Time = time;
                    setup.Shards.Answered = _ => time.Advance(TimeSpan.FromMinutes(1));
                    break;
            }
        });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid&_summary=count");

        AssertFailure(result, status, issue);
        host.Shards.Calls.ShouldContain(c => c.Shard == "east" && c.PageSize == 0);
        if (failure is "outage" or "no-total" or "overflow")
        {
            host.Shards.Calls.ShouldContain(c => c.Shard == "west" && c.PageSize == 0);
        }

        result.Body.ShouldNotContain("secret-path");
    }

    [Fact]
    public async Task CountOnly_RequiresAuthenticationBeforeCallingShards()
    {
        using var host = StartCountHost();

        SearchResult result = await host.GetAsync("/Patient?name=Orchid&_count=0", token: null);

        AssertFailure(result, HttpStatusCode.Unauthorized, "login");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CountOnly_PropagatesCallerCancellationToTheShards()
    {
        using var host = StartCountHost(setup => setup.Shards.Hang = true);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(30));

        Task<SearchResult> search = host.GetCancellableAsync("/Patient?name=Orchid&_count=0", host.Token(), caller.Token);
        await host.Shards.Hanging.Task.WaitAsync(caller.Token);
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(search);
        await host.Shards.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PagedTotal_RemainsUnsupportedEvenWithCountProofAndDialects()
    {
        using var host = StartCountHost();

        SearchResult result = await host.GetAsync("/Patient?name=Orchid&_total=accurate");

        AssertFailure(result, HttpStatusCode.NotImplemented, "not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    private static SearchHost StartCountHost(Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = CertifiedView();
            setup.ConfigureServices = services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(Dialects());
            // The fixture contains Patient/dup on each shard. Each owner counts its own Orchid match.
            setup.Shards.CountAnswer = request => new ShardPage([], null,
                request.Parameters.Any(p => p.Key == "name" && p.Value == "Orchid") ? 1 : 0);
            configure?.Invoke(setup);
        });

    private static SearchCapabilities CertifiedView(string? withoutProof = null) =>
        SearchWorld.Capabilities(exactCountShapes: ["Patient?name"], withoutExactCountProof: withoutProof);

    private static Dictionary<ShardId, CountDialect> Dialects() => new()
    {
        [ShardId.Parse("east")] = CountDialect.CountZeroWithSummaryCount,
        [ShardId.Parse("west")] = CountDialect.CountZero,
    };

    private static Task<SearchResult> CountAsync(SearchHost host, bool post, string query) =>
        post ? host.PostFormAsync("/Patient/_search", query) : host.GetAsync($"/Patient?{query}");

    private static void AssertFailure(SearchResult result, HttpStatusCode status, string issue)
    {
        result.StatusCode.ShouldBe(status);
        result.MediaType.ShouldBe("application/fhir+json");
        result.IssueCode.ShouldBe(issue);
        result.Json.TryGetProperty("total", out _).ShouldBeFalse();
    }
}

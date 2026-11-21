using System.Net;
using System.Text;
using System.Text.Json;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Budgets;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>Exercises count-only distributed depth-one <c>_has</c> searches through the authenticated HTTP routes.</summary>
public sealed class PrimarySearchReverseCountTests
{
    private const string Has = "_has:Observation:subject:code=oracle-code";
    private const string RepeatedShape = "Patient?_id=,&_id=,&name";

    [Theory]
    [InlineData(false, "_count=0")]
    [InlineData(true, "_summary=count")]
    public async Task EqualIdsOnDifferentShardsAreTwoResourcesAndEveryOwnerIsCountedOnce(bool post, string control)
    {
        using SearchHost host = Start();
        string query = $"{Has}&{control}";

        SearchResult result = await SendAsync(host, post, query);

        AssertCount(result, 2);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?{query}");
        host.Shards.Views.Count.ShouldBe(1);
        host.Shards.Calls.Where(c => c.Parameters == "code=oracle-code").Select(c => c.Shard)
            .Order(StringComparer.Ordinal).ShouldBe(["east", "west"]);
        ShardCall[] counts = [.. host.Shards.Calls.Where(c => c.PageSize == 0)];
        counts.Length.ShouldBe(2);
        ShardCall east = counts.Single(c => c.Shard == "east");
        (east.Method, east.Parameters).ShouldBe((ShardSearchMethod.PostSearch, "_id=dup&_summary=count"));
        ShardCall west = counts.Single(c => c.Shard == "west");
        (west.Method, west.Parameters).ShouldBe((ShardSearchMethod.Get, "_id=dup"));
    }

    [Fact]
    public async Task UnwitnessedShardGetsNoCountCallAndTheOwnerCountIsTheTotal()
    {
        using SearchHost host = Start(configure: s => s.Shards.Resources = ForeignWitnessCorpus());

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        AssertCount(result, 2);
        ShardCall count = host.Shards.Calls.Where(c => c.PageSize == 0).ShouldHaveSingleItem();
        (count.Shard, count.Method, count.Parameters).ShouldBe(("east", ShardSearchMethod.PostSearch, "_id=dup%2Ceast-only&_summary=count"));
    }

    [Fact]
    public async Task EqualIdsWitnessedOnDifferentOwnersAcrossClausesAreNotTheSameResource()
    {
        using SearchHost host = Start(shapes: ["Patient?_id=,&_id=,"], configure: s => s.Shards.Resources = DisjointOwnerWitnessCorpus());

        SearchResult result = await SendAsync(host, false,
            "_has:Observation:subject:_id=east-witness&_has:Observation:subject:_id=west-witness&_count=0");

        // Each clause names Patient/dup, but on a different shard: no physical owner satisfies both, so there is nothing to count.
        AssertCount(result, 0);
        host.Shards.Calls.Where(c => c.PageSize == 0).ShouldBeEmpty();
        host.Shards.Calls.Select(c => c.Parameters).ShouldAllBe(p => p == "_id=east-witness" || p == "_id=west-witness");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCountSizeIsMeasuredOnTheExactGeneratedRequestAndNoCountIsSent(bool post)
    {
        using SearchHost host = Start();
        var measured = new List<ShardSearchRequest>();
        host.Shards.RequestFits = request =>
        {
            measured.Add(request);
            return request.Shard.Value != "west";
        };

        SearchResult result = await SendAsync(host, post, Has + "&_count=0");

        AssertFailure(result, HttpStatusCode.BadRequest, "too-costly");
        // East's dialect is POST with _summary=count, west's is GET: each is measured in its own verb before any count is sent.
        measured.Select(r => (r.Shard.Value, r.Method, r.PageSize, string.Join('&', r.Parameters.Select(p => $"{p.Key}={p.Value}")))).ShouldBe(
        [
            ("east", ShardSearchMethod.PostSearch, 0, "_id=dup&_summary=count"),
            ("west", ShardSearchMethod.Get, 0, "_id=dup"),
        ]);
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "code=oracle-code");
    }

    [Fact]
    public async Task NoWitnessKeysAreAnExactZeroWithoutAnyCountCall()
    {
        using SearchHost host = Start();

        SearchResult result = await SendAsync(host, false, "_has:Observation:subject:_id=nonexistent&_count=0");

        AssertCount(result, 0);
        host.Shards.Calls.Select(c => c.Parameters).ShouldBe(["_id=nonexistent", "_id=nonexistent"]);
    }

    [Fact]
    public async Task DanglingWitnessIsStillCountedByItsOwnerAndMayBeZero()
    {
        using SearchHost host = Start(configure: s => s.Shards.Resources = DanglingWitnessCorpus());

        SearchResult result = await SendAsync(host, true, "_has:Observation:subject:_id=ghost-witness&_summary=count");

        AssertCount(result, 0);
        ShardCall count = host.Shards.Calls.Where(c => c.PageSize == 0).ShouldHaveSingleItem();
        (count.Shard, count.Parameters).ShouldBe(("east", "_id=ghost&_summary=count"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeptAndRepeatedPredicatesAreCountedAsTheyWereGeneratedForEachOwner(bool post)
    {
        using SearchHost host = Start(shapes: [RepeatedShape]);
        string query = Has + "&_has:Observation:subject:_id=obs-dup&name=%45li&_count=0";

        SearchResult result = await SendAsync(host, post, query);

        // Both shards hold Patient/dup, but only east's is named Eli: the kept name predicate is what makes the total 1, not 2.
        AssertCount(result, 1);
        // A GET goes through the client's URI normalisation, which decodes the unreserved %45 before it arrives; a form body is verbatim.
        // Whatever arrived is echoed in the self link and kept as is on the wire.
        string arrived = post ? "%45" : "E";
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?{query.Replace("%45", arrived, StringComparison.Ordinal)}");
        ShardCall[] counts = [.. host.Shards.Calls.Where(c => c.PageSize == 0)];
        ShardCall east = counts.Single(c => c.Shard == "east");
        (east.Method, east.Parameters).ShouldBe((ShardSearchMethod.Get, $"_id=dup&_id=dup&name={arrived}li&_summary=count"));
        ShardCall west = counts.Single(c => c.Shard == "west");
        (west.Method, west.Parameters).ShouldBe((ShardSearchMethod.Get, $"_id=dup&_id=dup&name={arrived}li"));
    }

    [Theory]
    [InlineData(false, "no-map")]
    [InlineData(true, "missing-dialect")]
    [InlineData(false, "undefined-dialect")]
    [InlineData(true, "proof")]
    public async Task MissingOrInvalidDialectMapOrProofRefusesBeforeAnyClientOrSend(bool post, string missing)
    {
        using SearchHost host = Start(
            dialects: missing is not "no-map",
            dialectMap: missing switch
            {
                "missing-dialect" => d => d.Remove(ShardId.Parse("west")),
                "undefined-dialect" => d => d[ShardId.Parse("west")] = (CountDialect)int.MaxValue,
                _ => null,
            },
            configure: s =>
            {
                if (missing == "proof")
                {
                    s.Capabilities = Capabilities(withoutProof: "west");
                }
            });

        SearchResult result = await SendAsync(host, post, Has + "&_count=0");

        AssertFailure(result, HttpStatusCode.BadRequest, "not-supported");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PrimaryTypePermissionPrecedesParsingAndAnyClient()
    {
        using SearchHost host = Start();
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole], ObjectId = SearchWorld.CallerObjectId });

        SearchResult result = await host.GetAsync("/Patient?" + Has + "&_count=not-a-number", token);

        AssertFailure(result, HttpStatusCode.Forbidden, "forbidden");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeniedWitnessTypeIsForbiddenBeforeAnyClient(bool dialects)
    {
        using SearchHost host = Start(dialects, configure: s => s.Settings.Remove($"Federation:Host:Roles:{SearchWorld.ReaderRole}:1"));

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        AssertFailure(result, HttpStatusCode.Forbidden, "forbidden");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnauthenticatedCallerReachesNoShard()
    {
        using SearchHost host = Start();

        SearchResult result = await host.GetAsync("/Patient?" + Has + "&_count=0", token: null);

        AssertFailure(result, HttpStatusCode.Unauthorized, "login");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("&_total=accurate")]
    [InlineData("&_count=5&_total=accurate")]
    [InlineData("&_count=5")]
    public async Task PagedSearchesStillNeverCountHere(string controls)
    {
        using SearchHost host = Start(dialects: false);

        SearchResult result = await SendAsync(host, false, Has + controls);

        result.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task WitnessOutageFailsTheWholeCountWithoutATotalOrACountCall()
    {
        using SearchHost host = Start(configure: s => s.Shards.FailingShard = "west");

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        AssertFailure(result, HttpStatusCode.BadGateway, "exception");
        result.Body.ShouldContain("p9xm");
        result.Body.ShouldNotContain("secret-path");
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "code=oracle-code");
    }

    [Theory]
    [InlineData("outage", HttpStatusCode.BadGateway)]
    [InlineData("no-total", HttpStatusCode.BadGateway)]
    [InlineData("overflow", HttpStatusCode.BadGateway)]
    public async Task CountFailureIsAllOrNothingAndLeaksNothing(string failure, HttpStatusCode status)
    {
        using SearchHost host = Start(configure: s =>
        {
            s.Settings["Federation:Limits:MaxParallelBackendCalls"] = "1";
            Func<ShardSearchRequest, ShardPage> natural = s.Shards.CountAnswer!;
            s.Shards.CountAnswer = failure switch
            {
                "outage" => request => request.Shard.Value == "west"
                    ? throw new HttpRequestException("scripted failure at https://fhir-west.example.org/secret-path")
                    : natural(request),
                "no-total" => request => new ShardPage([], null, request.Shard.Value == "east" ? 1 : null),
                _ => _ => new ShardPage([], null, int.MaxValue),
            };
        });

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        AssertFailure(result, status, "exception");
        result.Body.ShouldNotContain("secret-path");
        host.Shards.Calls.Count(c => c.PageSize == 0).ShouldBe(2);
    }

    [Theory]
    [InlineData("1", 1, false)]
    [InlineData("2", 1, true)]
    [InlineData("2", 2, false)]
    public async Task CountSpendsOnlyWhatTheWitnessScansLeftOfTheLogicalAllowance(string perShard, int sends, bool counted)
    {
        using SearchHost host = Start(configure: s =>
        {
            s.Settings["Federation:Limits:BackendCallsPerShard"] = perShard;
            s.Shards.SendsPerCall = sends;
        });

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        // The allowance is perShard * 2 shards. Two witness scans spend max(physical sends, logical calls); both counts need two more,
        // and a count that does not fit is refused before it is sent.
        if (counted)
        {
            AssertCount(result, 2);
        }
        else
        {
            AssertFailure(result, HttpStatusCode.BadRequest, "too-costly");
            host.Shards.Calls.ShouldAllBe(c => c.Parameters == "code=oracle-code");
        }
    }

    [Fact]
    public async Task LiveDeadlinePassingDuringTheCountIsATimeoutNotATotal()
    {
        var time = new ManualTimeProvider { TimersEnabled = false };
        using SearchHost host = Start(configure: s =>
        {
            s.Time = time;
            Func<ShardSearchRequest, ShardPage> natural = s.Shards.CountAnswer!;
            s.Shards.CountAnswer = request =>
            {
                time.Advance(TimeSpan.FromMinutes(1));
                return natural(request);
            };
        });

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        AssertFailure(result, HttpStatusCode.GatewayTimeout, "timeout");
        host.Shards.Calls.ShouldContain(c => c.PageSize == 0);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToTheShards()
    {
        using SearchHost host = Start(configure: s => s.Shards.Hang = true);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(30));

        Task<SearchResult> search = host.GetCancellableAsync("/Patient?" + Has + "&_count=0", host.Token(), caller.Token);
        await host.Shards.Hanging.Task.WaitAsync(caller.Token);
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(search);
        await host.Shards.ObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToTheOwnerCountCalls()
    {
        // Discovery answers; only the owner count calls are parked, so cancellation can only reach the count phase's calls.
        using SearchHost host = Start(configure: s => s.Shards.ParkCounts = true);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            Task<SearchResult> search = host.GetCancellableAsync("/Patient?" + Has + "&_count=0", host.Token(), caller.Token);
            await AssertSignalledAsync(host.Shards.Hanging.Task, "an owner count call never parked");
            host.Shards.Calls.Count(c => c.Parameters == "code=oracle-code").ShouldBe(2);
            host.Shards.Calls.Count(c => c.Method is not null && c.PageSize == 0).ShouldBeGreaterThan(0);
            await caller.CancelAsync();

            // Observe the shard first, on a bounded wait: the in-memory client answers only once the server has finished.
            await AssertSignalledAsync(host.Shards.ObservedCancellation.Task, "caller cancellation never reached an owner count call");
            await Should.ThrowAsync<OperationCanceledException>(search);
        }
        finally
        {
            host.Shards.ReleaseParked();
        }
    }

    private static async Task AssertSignalledAsync(Task signal, string because)
    {
        Task first = await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        first.ShouldBeSameAs(signal, because);
    }

    private static void AssertCount(SearchResult result, int total)
    {
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.MediaType.ShouldBe("application/fhir+json");
        result.Json.GetProperty("type").GetString().ShouldBe("searchset");
        result.Json.GetProperty("total").GetInt32().ShouldBe(total);
        result.Json.TryGetProperty("entry", out _).ShouldBeFalse();
        result.Link("next").ShouldBeNull();
        result.Json.GetProperty("link").GetArrayLength().ShouldBe(1);
    }

    private static void AssertFailure(SearchResult result, HttpStatusCode status, string issue)
    {
        result.StatusCode.ShouldBe(status);
        result.MediaType.ShouldBe("application/fhir+json");
        result.IssueCode.ShouldBe(issue);
        result.Json.TryGetProperty("total", out _).ShouldBeFalse();
        result.Json.TryGetProperty("entry", out _).ShouldBeFalse();
    }

    private static Task<SearchResult> SendAsync(SearchHost host, bool post, string query) =>
        post ? host.PostFormAsync("/Patient/_search", query) : host.GetAsync("/Patient?" + query);

    private static Dictionary<string, IReadOnlyList<SearchWorld.SeedResource>> ForeignWitnessCorpus()
    {
        var resources = SearchWorld.Resources.ToDictionary(StringComparer.Ordinal);
        const string witness = """
            {"resourceType":"Observation","id":"east-witness","code":{"coding":[{"code":"oracle-code"}]},
            "subject":{"reference":"https://fhir-east.example.org/Patient/east-only"}}
            """;
        resources["west"] = [.. resources["west"].Where(r => r.Id != "obs-dup"),
            new("Observation", "east-witness", Encoding.UTF8.GetBytes(witness))];
        return resources;
    }

    private static Dictionary<string, IReadOnlyList<SearchWorld.SeedResource>> DisjointOwnerWitnessCorpus()
    {
        var resources = SearchWorld.Resources.ToDictionary(StringComparer.Ordinal);
        static SearchWorld.SeedResource Witness(string id) => new("Observation", id, Encoding.UTF8.GetBytes(
            $$$"""{"resourceType":"Observation","id":"{{{id}}}","subject":{"reference":"Patient/dup"}}"""));
        resources["east"] = [.. resources["east"], Witness("east-witness")];
        resources["west"] = [.. resources["west"], Witness("west-witness")];
        return resources;
    }

    private static Dictionary<string, IReadOnlyList<SearchWorld.SeedResource>> DanglingWitnessCorpus()
    {
        var resources = SearchWorld.Resources.ToDictionary(StringComparer.Ordinal);
        const string witness = """
            {"resourceType":"Observation","id":"ghost-witness","subject":{"reference":"Patient/ghost"}}
            """;
        resources["east"] = [.. resources["east"], new("Observation", "ghost-witness", Encoding.UTF8.GetBytes(witness))];
        return resources;
    }

    /// <summary>Starts a host whose shards count the generated <c>_id</c> predicates against their own seeded patients.</summary>
    private static SearchHost Start(
        bool dialects = true,
        Action<Dictionary<ShardId, CountDialect>>? dialectMap = null,
        IEnumerable<string>? shapes = null,
        Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = Capabilities(shapes);
            setup.Shards.FilterGeneratedIds = true;
            setup.Shards.CountAnswer = request => new ShardPage([], null, setup.Shards.Resources[request.Shard.Value].Count(patient =>
                patient.Type == "Patient"
                && request.Parameters.Where(p => p.Key == "_id").All(p => Uri.UnescapeDataString(p.Value).Split(',').Contains(patient.Id))
                && request.Parameters.Where(p => p.Key == "name").All(p => HasName(patient, Uri.UnescapeDataString(p.Value)))));
            configure?.Invoke(setup);
            if (dialects)
            {
                var map = new Dictionary<ShardId, CountDialect>
                {
                    [ShardId.Parse("east")] = CountDialect.CountZeroWithSummaryCount,
                    [ShardId.Parse("west")] = CountDialect.CountZero,
                };
                dialectMap?.Invoke(map);
                setup.ConfigureServices = services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(map);
            }
        });

    /// <summary>Evaluates the string-search <c>name</c> predicate: a given or family name starting with the value, ignoring case.</summary>
    private static bool HasName(SearchWorld.SeedResource patient, string value)
    {
        using JsonDocument document = JsonDocument.Parse(patient.Json);
        return document.RootElement.GetProperty("name").EnumerateArray()
            .SelectMany(name => name.EnumerateObject().Where(p => p.Name is "family" or "given"))
            .SelectMany(part => part.Value.ValueKind == JsonValueKind.Array ? part.Value.EnumerateArray().ToArray() : [part.Value])
            .Any(text => text.GetString()!.StartsWith(value, StringComparison.OrdinalIgnoreCase));
    }

    private static SearchCapabilities Capabilities(IEnumerable<string>? shapes = null, string? withoutProof = null) =>
        SearchWorld.Capabilities(exactCountShapes: ["Patient?_id=,", .. shapes ?? []], reverseChain: true, withoutExactCountProof: withoutProof);
}

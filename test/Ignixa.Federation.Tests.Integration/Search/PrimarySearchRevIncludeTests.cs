using System.Net;
using System.Text;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Budgets;
using Ignixa.Federation.Tests.Integration.Continuations;
using Ignixa.Federation.Tests.Integration.Reads;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// Public paged <c>_revinclude</c> on the unsorted search path: what the host admits, what it hands the optional page
/// resolver (only the emitted page, the current plan and grants, the request's own client and budget), and how every failure
/// of the extra stage is a whole-request outcome. The resolver is scripted; no traversal is re-implemented here.
/// </summary>
public sealed class PrimarySearchRevIncludeTests
{
    private const string Rev = "_revinclude=Observation:subject";
    private const string Paged = "name=Orchid&" + Rev + "&_count=1";
    private const string Mixed = "name=Orchid&" + Rev + "&_revinclude=Condition:subject&_count=1";
    private const string CountShape = "Patient?name";
    private const string EastObservation = "https://fhir-east.example.org/Observation/obs-dup";
    private const string WestObservation = "https://fhir-west.example.org/Observation/obs-dup";
    private const string EastCondition = "https://fhir-east.example.org/Condition/cond-1";
    private const string ObservationGrant = $"Federation:Host:Roles:{SearchWorld.ReaderRole}:1";
    private const string ConditionGrant = $"Federation:Host:Roles:{SearchWorld.ReaderRole}:2";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothMethodsPageTheOwnerCutsAndTheResolverSeesOnlyTheEmittedPage(bool post)
    {
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost host = Start(resolver);

        SearchResult first = await SendAsync(host, post, Paged);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string[] page = [.. Parse(first).Where(e => e.Mode == "match").Select(e => e.FullUrl)];
        page.Length.ShouldBe(1);
        ResolverCall firstCall = resolver.Calls.ShouldHaveSingleItem();
        Urls(firstCall.Matches).ShouldBe(page);
        first.Link("self").ShouldBe(SearchWorld.PublicBase + "/Patient?" + Paged);
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == null || !c.Parameters.Contains("revinclude", StringComparison.OrdinalIgnoreCase));
        host.Shards.Views.Count.ShouldBe(1);

        SearchResult second = await host.FollowAsync(first.Link("next").ShouldNotBeNull());

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        string[] secondPage = [.. Parse(second).Where(e => e.Mode == "match").Select(e => e.FullUrl)];
        secondPage.Length.ShouldBe(1);
        resolver.Calls.Count.ShouldBe(2);
        Urls(resolver.Calls[1].Matches).ShouldBe(secondPage);
        secondPage.ShouldNotContain(page[0]);
        second.Link("self").ShouldBe(SearchWorld.PublicBase + "/Patient?" + Paged);
        host.Shards.Calls.Skip(1).ShouldAllBe(c => c.Parameters == null || !c.Parameters.Contains("revinclude", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncludesAreTheResolversExactBytesUnderEachPhysicalOwner(bool proxyReads)
    {
        var resolver = new ScriptedRevIncludeResolver(ScriptedRevIncludeResolver.Returning(false,
            ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup"),
            ScriptedRevIncludeResolver.Entry("west", "Observation", "obs-dup")));
        using SearchHost host = Start(resolver, proxyReads
            ? s => s.ConfigureServices = services => services.AddSingleton<IShardReadBinding>(new ScriptedReadBinding())
            : null);

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = Parse(result);
        entries.Select(e => e.Mode).ShouldBe(["match", "include", "include"]);
        entries.Skip(1).Select(e => e.FullUrl).ShouldBe([EastObservation, WestObservation]);
        result.Entries.Skip(1).Select(e => e.Resource).ShouldBe(
        [
            Encoding.UTF8.GetString(SearchWorld.Slice("east", "Observation", "obs-dup")),
            Encoding.UTF8.GetString(SearchWorld.Slice("west", "Observation", "obs-dup")),
        ]);
        if (proxyReads)
        {
            entries.Skip(1).Select(e => e.Alternate).ShouldBe(
            [
                SearchWorld.PublicBase + "/shards/k7q2/Observation/obs-dup",
                SearchWorld.PublicBase + "/shards/p9xm/Observation/obs-dup",
            ]);
        }
        else
        {
            result.Body.ShouldNotContain("alternate");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutABindingAValidRevIncludeIsNotSupportedBeforeAnyClientOrShardIo(bool post)
    {
        using SearchHost host = Start(null);

        SearchResult refused = await SendAsync(host, post, Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        refused.IssueCode.ShouldBe("not-supported");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RedeemingOnAHostWithoutABindingIsNotSupportedNotARestart()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        using SearchHost unbound = Start(null, s => s.Protection = ring.CreateProvider());
        string next = (await SendAsync(issuer, false, Paged)).Link("next").ShouldNotBeNull();

        SearchResult refused = await unbound.FollowAsync(next);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        unbound.Shards.Views.ShouldBeEmpty();
        unbound.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("total")]
    [InlineData("count zero")]
    [InlineData("summary count")]
    [InlineData("sort")]
    [InlineData("true include")]
    [InlineData("reverse chain")]
    [InlineData("forward chain")]
    public async Task OnlyTheOrdinaryUnsortedPagedShapeIsAdmitted(string shape)
    {
        var resolver = new ScriptedRevIncludeResolver();
        string query = Paged;
        using SearchHost host = Start(resolver, s =>
        {
            switch (shape)
            {
                case "total":
                    // The exact-count shape of this query is certified on every owner, so the planner admits it.
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, exactCountShapes: [CountShape]);
                    query = Paged + "&_total=accurate";
                    break;
                case "count zero":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, exactCountShapes: [CountShape]);
                    query = "name=Orchid&" + Rev + "&_count=0";
                    break;
                case "summary count":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, exactCountShapes: [CountShape]);
                    query = Paged + "&_summary=count";
                    break;
                case "sort":
                    // Every owner certifies a stable _lastUpdated order, so the planner admits the sort and the host decides.
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, registry: SearchWorld.SortableRegistry);
                    query = Paged + "&_sort=_lastUpdated";
                    break;
                case "true include":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, organizationInclude: true);
                    s.Settings[$"Federation:Host:Roles:{SearchWorld.ReaderRole}:2"] = "Organization";
                    query = Paged + "&_include=Patient:organization";
                    break;
                case "reverse chain":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, reverseChain: true, exactCountShapes: ["Patient?", "Patient?_id=,"]);
                    s.ConfigureServices = services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(
                        s.Capabilities!.Registry.Shards.ToDictionary(shard => shard.Id, _ => CountDialect.CountZero));
                    query = "_has:Observation:subject:code=oracle-code&" + Rev + "&_count=1";
                    break;
                case "forward chain":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, organizationForward: true);
                    s.Settings["Federation:ChainResolution:Passthrough:0"] = "Patient.organization:Organization";
                    s.Settings[$"Federation:Host:Roles:{SearchWorld.ReaderRole}:2"] = "Organization";
                    query = "organization.name=Acme&" + Rev + "&_count=1";
                    break;
                default:
                    throw new InvalidOperationException("Unknown test case.");
            }
        });

        SearchResult refused = await host.GetAsync("/Patient?" + query);

        // A planner refusal is a 400; only the host's own admission gate answers 501 for a valid search it does not execute.
        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        refused.IssueCode.ShouldBe("not-supported");
        refused.Body.ShouldNotContain("Bundle");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
        resolver.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheRedeemedTokenPinsEveryRequestedSourceReferenceDefinitionAndEveryRegistryMember()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost host = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        SearchResult first = await SendAsync(host, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        HashSet<(ContinuationDependencyGroup Group, string Key)> consulted = Consult(ring, first.Link("next").ShouldNotBeNull());

        consulted.ShouldContain((ContinuationDependencyGroup.Definition, "Observation.subject"));
        consulted.ShouldContain((ContinuationDependencyGroup.Definition, "Patient.name"));
        consulted.Where(c => c.Group == ContinuationDependencyGroup.Shard).Select(c => c.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["east", "west"]);
    }

    [Fact]
    public async Task ASourceDefinitionRevisionThatNoLongerMatchesRestartsWithoutAClient()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        using SearchHost redeemer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        string next = (await SendAsync(issuer, false, Paged)).Link("next").ShouldNotBeNull();
        var (protector, issued, view) = Redeem(ring, next);
        List<KeyValuePair<string, string>> shards = [];
        List<KeyValuePair<string, string>> definitions = [];
        foreach ((ContinuationDependencyGroup group, string key) in Consult(ring, next))
        {
            string current = Revision(view, group, key).ShouldNotBeNull();
            (group == ContinuationDependencyGroup.Shard ? shards : definitions)
                .Add(KeyValuePair.Create(key, key == "Observation.subject" ? "stale" : current));
        }

        ContinuationIssue stale = protector.Issue(issued.Binding, ContinuationDependencySet.Create(shards, definitions), issued.Snapshot.AsSpan());

        SearchResult refused = await redeemer.GetAsync("/Patient?ct=" + stale.InlineToken.ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        redeemer.Shards.Views.ShouldBeEmpty();
        redeemer.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("withdrawn proof")]
    [InlineData("registry growth")]
    public async Task ProofWithdrawalAndRegistryGrowthRestartWithoutAClientOrResolverWork(string drift)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost redeemer = Start(resolver, s =>
        {
            s.Protection = ring.CreateProvider();
            if (drift == "withdrawn proof")
            {
                s.Capabilities = SearchWorld.Capabilities(reverseChain: true);
                return;
            }

            s.Capabilities = SearchWorld.Capabilities(revInclude: true, registry: SearchWorld.ReverseRegistry(extraWitnessShard: true));
            foreach (var (key, value) in SearchWorld.Settings(extraWitnessShard: true))
            {
                s.Settings[key] = value;
            }
        });
        string next = (await SendAsync(issuer, false, Paged)).Link("next").ShouldNotBeNull();

        SearchResult refused = await redeemer.FollowAsync(next);

        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        redeemer.Shards.Views.ShouldBeEmpty();
        redeemer.Shards.Calls.ShouldBeEmpty();
        resolver.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARedeemedTokenIsReplannedFromTheRawQueryAgainstTheCurrentCertifiedPath()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost redeemer = Start(resolver, s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = SearchWorld.Capabilities(revInclude: true, forwardChain: true);
        });
        string next = (await SendAsync(issuer, false, Paged)).Link("next").ShouldNotBeNull();

        SearchResult second = await redeemer.FollowAsync(next);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        ResolverCall call = resolver.Calls.ShouldHaveSingleItem();
        call.Plan.Type.Value.ShouldBe("Patient");
        call.Plan.Occurrences.Count(o => o.Kind == OccurrenceKind.RevInclude).ShouldBe(1);
        Urls(call.Matches).ShouldBe(Parse(second).Where(e => e.Mode == "match").Select(e => e.FullUrl));
    }

    [Fact]
    public async Task ADeniedIncludeTypeOmitsTheIncludesWithAGenericOutcomeAndNeverAsksTheResolver()
    {
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost host = Start(resolver, s => s.Settings.Remove(ObservationGrant));

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = Parse(result);
        entries.Select(e => e.Mode).ShouldBe(["match", "outcome"]);
        entries[1].Code.ShouldBe("suppressed");
        resolver.Calls.ShouldBeEmpty();
        result.Link("next").ShouldNotBeNull();
        string outcome = result.Entries[1].Resource;
        outcome.ShouldNotContain("Observation");
        outcome.ShouldNotContain("obs-dup");
    }

    [Fact]
    public async Task MixedGrantsPassTheExactPlanAndOnlyThePrimaryAndAuthorizedSourceTypes()
    {
        var resolver = new ScriptedRevIncludeResolver(ScriptedRevIncludeResolver.Returning(false, ConditionEntry()));
        using SearchHost host = Start(resolver, s =>
        {
            s.Capabilities = SearchWorld.Capabilities(revInclude: true, conditionRevInclude: true);
            s.Settings.Remove(ObservationGrant);
            s.Settings[ConditionGrant] = "Condition";
        });

        SearchResult result = await SendAsync(host, false, Mixed);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        ResolverCall call = resolver.Calls.ShouldHaveSingleItem();
        call.Readable.Select(t => t.Value).ShouldBe(["Condition", "Patient"], ignoreOrder: true);
        call.Plan.Occurrences.Where(o => o.Kind == OccurrenceKind.RevInclude).Select(o => o.Hop!.Path.Source.Value)
            .ShouldBe(["Observation", "Condition"], ignoreOrder: true);
        var entries = Parse(result);
        entries.Select(e => e.Mode).ShouldBe(["match", "include", "outcome"]);
        entries[1].FullUrl.ShouldBe(EastCondition);
        entries[2].Code.ShouldBe("suppressed");
        result.Entries[2].Resource.ShouldNotContain("Observation");
    }

    [Fact]
    public async Task AGrantDowngradeOnTheSameTokenKeepsThePrimaryAndOmitsTheNowDeniedIncludes()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(new ScriptedRevIncludeResolver(), s => s.Protection = ring.CreateProvider());
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost downgraded = Start(resolver, s =>
        {
            s.Protection = ring.CreateProvider();
            s.Settings.Remove(ObservationGrant);
        });
        SearchResult first = await SendAsync(issuer, false, Paged);
        first.Entries.Count.ShouldBe(2);

        SearchResult second = await downgraded.FollowAsync(first.Link("next").ShouldNotBeNull());

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        Parse(second).Select(e => e.Mode).ShouldBe(["match", "outcome"]);
        resolver.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoMatchesMeansNoResolverWorkAndNoOutcome()
    {
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost host = Start(resolver);
        host.Shards.FilterGeneratedIds = true;

        SearchResult result = await host.GetAsync("/Patient?_id=nobody&" + Rev + "&_count=1");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        Parse(result).ShouldBeEmpty();
        resolver.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ungranted type")]
    [InlineData("unplanned type")]
    [InlineData("over the limit")]
    public async Task ABindingResultOutsideTheGrantPlanOrLimitIsRejectedWithoutBeingSerialized(string defect)
    {
        var resolver = new ScriptedRevIncludeResolver(ScriptedRevIncludeResolver.Returning(false,
            defect == "unplanned type" ? ConditionEntry() : ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup"),
            ScriptedRevIncludeResolver.Entry("west", "Observation", "obs-dup")));
        using SearchHost host = Start(resolver, s =>
        {
            switch (defect)
            {
                case "ungranted type":
                    s.Capabilities = SearchWorld.Capabilities(revInclude: true, conditionRevInclude: true);
                    s.Settings.Remove(ObservationGrant);
                    s.Settings[ConditionGrant] = "Condition";
                    break;
                case "over the limit":
                    s.Settings["Federation:Limits:MaxIncludesPerPage"] = "1";
                    break;
            }
        });

        SearchResult result = await SendAsync(host, false, defect == "ungranted type" ? Mixed : Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.Body.ShouldNotContain("Bundle");
        result.Body.ShouldNotContain("obs-dup");
        result.Body.ShouldNotContain("cond-1");
        result.Link("next").ShouldBeNull();
    }

    [Theory]
    [InlineData("duplicate include")]
    [InlineData("include that is a match")]
    [InlineData("match at another version")]
    public async Task ADuplicateIncludeOrAnIncludeThatIsAMatchIsRejectedWithoutBeingSerialized(string defect)
    {
        var resolver = new ScriptedRevIncludeResolver(call =>
        {
            ShardEntry match = call.Matches[0];
            ShardEntry[] includes = defect switch
            {
                "duplicate include" => [ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup"), ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup")],
                "include that is a match" => [match],
                _ => [new ShardEntry(match.Key, "another-version", null, match.Resource.Span)],
            };
            return Task.FromResult<RevIncludePageResult?>(new RevIncludePageResult(includes, false));
        });
        using SearchHost host = Start(resolver, s =>
        {
            if (defect != "duplicate include")
            {
                s.Capabilities = SearchWorld.Capabilities(selfRevInclude: true);
            }
        });

        SearchResult result = await SendAsync(host, false, defect == "duplicate include" ? Paged : "name=Orchid&_revinclude=Patient:link&_count=1");

        resolver.Calls.ShouldHaveSingleItem();
        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.IssueCode.ShouldBe("exception");
        result.Body.ShouldNotContain("Bundle");
        result.Body.ShouldNotContain("obs-dup");
        result.Body.ShouldNotContain("/Patient/dup");
        result.Link("next").ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactFillIsSilentAndOnlyAnExtraEligibleEntryAddsOneIncompleteOutcome(bool truncated)
    {
        var resolver = new ScriptedRevIncludeResolver(ScriptedRevIncludeResolver.Returning(truncated,
            ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup"),
            ScriptedRevIncludeResolver.Entry("west", "Observation", "obs-dup")));
        using SearchHost host = Start(resolver, s => s.Settings["Federation:Limits:MaxIncludesPerPage"] = "2");

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = Parse(result);
        entries.Count(e => e.Mode == "include").ShouldBe(2);
        entries.Where(e => e.Mode == "outcome").Select(e => e.Code).ShouldBe(truncated ? ["incomplete"] : []);
    }

    [Fact]
    public async Task TheResolverGetsTheRequestsOwnClientRegistryLimitsAndRemainingAllowance()
    {
        RequestBudget? budget = null;
        bool sameToken = false;
        SearchHost? host = null;
        int callsAtResolve = 0;
        var resolver = new ScriptedRevIncludeResolver(call =>
        {
            sameToken = call.Token == budget!.Token;
            callsAtResolve = host!.Shards.Calls.Count;
            return Task.FromResult<RevIncludePageResult?>(new RevIncludePageResult([], false));
        });
        host = Start(resolver, s =>
        {
            s.Settings["Federation:Limits:BackendCallsPerShard"] = "10";
            s.Settings["Federation:Limits:MaxParallelBackendCalls"] = "3";
            s.Settings["Federation:Limits:MaxIncludesPerPage"] = "7";
            s.Shards.SendsPerCall = 2;
            s.Shards.Answered = b => budget = b;
            s.Shards.RequestFits = r => r.PageSize < 5;
        });
        using (host)
        {
            SearchResult result = await SendAsync(host, false, Paged);

            result.StatusCode.ShouldBe(HttpStatusCode.OK);
            ResolverCall call = resolver.Calls.ShouldHaveSingleItem();
            host.Shards.Views.Count.ShouldBe(1);
            call.Registry.ShouldBeSameAs(host.Shards.Views.Single().Registry);
            call.MaxParallel.ShouldBe(3);
            call.IncludeLimit.ShouldBe(7);
            call.RemainingFetches.ShouldBe(20 - (2 * callsAtResolve));
            sameToken.ShouldBeTrue();
            call.Fits(Request(1)).ShouldBeTrue();
            call.Fits(Request(6)).ShouldBeFalse();
            host.Shards.Calls.Count.ShouldBe(callsAtResolve);
        }
    }

    [Theory]
    [InlineData("physical calls", HttpStatusCode.BadRequest, "too-costly")]
    [InlineData("fetch budget", HttpStatusCode.BadRequest, "too-costly")]
    [InlineData("bytes", HttpStatusCode.BadRequest, "too-costly")]
    [InlineData("deadline", HttpStatusCode.GatewayTimeout, "timeout")]
    [InlineData("shard failed", HttpStatusCode.BadGateway, "exception")]
    [InlineData("shard timeout", HttpStatusCode.GatewayTimeout, "timeout")]
    public async Task WholeRequestFailuresOfTheIncludeStageReturnNoBundleAndNoNextLink(string failure, HttpStatusCode status, string issue)
    {
        var time = new ManualTimeProvider();
        RequestBudget? budget = null;
        var resolver = new ScriptedRevIncludeResolver(async call =>
        {
            switch (failure)
            {
                case "physical calls":
                    for (int i = 0; i < 200; i++)
                    {
                        await call.Client.SearchAsync(Request(1), call.Token);
                    }

                    break;
                case "fetch budget":
                    throw new FetchBudgetExhaustedException(3);
                case "bytes":
                    budget!.OnResponseBytesConsumed(10_000_000);
                    break;
                case "deadline":
                    time.Advance(TimeSpan.FromMinutes(5));
                    break;
                case "shard failed":
                    throw new ShardFailureException(ShardId.Parse("west"), ShardFailureReason.Failed, null);
                case "shard timeout":
                    throw new ShardFailureException(ShardId.Parse("west"), ShardFailureReason.Timeout, null);
            }

            return new RevIncludePageResult([ScriptedRevIncludeResolver.Entry("east", "Observation", "obs-dup")], false);
        });
        using SearchHost host = Start(resolver, s =>
        {
            s.Time = time;
            s.Shards.Answered = b => budget = b;
            s.Settings["Federation:Limits:MaxResponseBytes"] = "1000000";
        });

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(status);
        result.IssueCode.ShouldBe(issue);
        result.Body.ShouldNotContain("Bundle");
        result.Body.ShouldNotContain("obs-dup");
        result.Link("next").ShouldBeNull();
    }

    [Fact]
    public async Task ANullResultIsInsufficientEvidenceNotEmptyIncludes_NotSupportedInitiallyAndRestartWhenRedeemed()
    {
        using var ring = new IsolatedKeyRing();
        var resolver = new ScriptedRevIncludeResolver();
        using SearchHost host = Start(resolver, s => s.Protection = ring.CreateProvider());
        SearchResult first = await SendAsync(host, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        resolver.Script = _ => Task.FromResult<RevIncludePageResult?>(null);

        SearchResult redeemed = await host.FollowAsync(next);
        SearchResult initial = await SendAsync(host, false, Paged);

        redeemed.StatusCode.ShouldBe(HttpStatusCode.Gone);
        initial.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        initial.IssueCode.ShouldBe("not-supported");
        initial.Body.ShouldNotContain("Bundle");
        redeemed.Body.ShouldNotContain("Bundle");
    }

    private static ShardEntry ConditionEntry() => new(
        new ResourceKey(ShardId.Parse("east"), ResourceType.Parse("Condition"), ResourceId.Parse("cond-1")), null, null,
        Encoding.UTF8.GetBytes("""{"resourceType":"Condition","id":"cond-1","subject":{"reference":"https://fhir-east.example.org/Patient/dup"}}"""));

    private static ShardSearchRequest Request(int pageSize) =>
        new(ShardId.Parse("east"), ResourceType.Parse("Observation"), [], pageSize, ShardSearchMethod.Get);

    private static IEnumerable<string> Urls(IEnumerable<ShardEntry> entries) => entries.Select(e => SearchWorld.Registry.FullUrl(e.Key));

    private static IReadOnlyList<(string Mode, string FullUrl, string? Alternate, string? Code)> Parse(SearchResult result) =>
        result.Json.TryGetProperty("entry", out System.Text.Json.JsonElement entries)
            ?
            [
                .. entries.EnumerateArray().Select(e => (
                    e.TryGetProperty("search", out System.Text.Json.JsonElement search) ? search.GetProperty("mode").GetString()! : "none",
                    e.GetProperty("fullUrl").GetString()!,
                    e.TryGetProperty("link", out System.Text.Json.JsonElement links)
                        ? links.EnumerateArray().Where(l => l.GetProperty("relation").GetString() == "alternate")
                            .Select(l => l.GetProperty("url").GetString()).SingleOrDefault()
                        : null,
                    e.GetProperty("resource").TryGetProperty("issue", out System.Text.Json.JsonElement issues)
                        ? issues[0].GetProperty("code").GetString()
                        : null)),
            ]
            : [];

    private static HashSet<(ContinuationDependencyGroup Group, string Key)> Consult(IsolatedKeyRing ring, string next)
    {
        SearchCapabilities view = Capabilities();
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        var seen = new HashSet<(ContinuationDependencyGroup, string)>();
        protector.Redeem(new Uri(next).Query["?ct=".Length..], Binding(), (group, key) =>
        {
            seen.Add((group, key));
            return Revision(view, group, key);
        }).Continuation.ShouldNotBeNull();
        return seen;
    }

    private static (ContinuationProtector Protector, RedeemedContinuation Issued, SearchCapabilities View) Redeem(IsolatedKeyRing ring, string next)
    {
        SearchCapabilities view = Capabilities();
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        RedeemedContinuation issued = protector.Redeem(new Uri(next).Query["?ct=".Length..], Binding(),
            (group, key) => Revision(view, group, key)).Continuation.ShouldNotBeNull();
        return (protector, issued, view);
    }

    private static string? Revision(SearchCapabilities view, ContinuationDependencyGroup group, string key) =>
        group == ContinuationDependencyGroup.Shard
            ? view.Registry.ComputeVersion([ShardId.Parse(key)])
            : key.Split('.', 2) is [string type, string code]
                ? view.GetResolvedDefinitionRevision(ResourceType.Parse(type), code)
                : null;

    private static ContinuationBinding Binding() =>
        ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject",
            SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]);

    private static SearchCapabilities Capabilities() => SearchWorld.Capabilities(revInclude: true);

    private static Task<SearchResult> SendAsync(SearchHost host, bool post, string query) =>
        post ? host.PostFormAsync("/Patient/_search", query) : host.GetAsync("/Patient?" + query);

    private static SearchHost Start(ScriptedRevIncludeResolver? resolver, Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = Capabilities();
            configure?.Invoke(setup);
            if (resolver is not null)
            {
                Action<IServiceCollection>? prior = setup.ConfigureServices;
                setup.ConfigureServices = services =>
                {
                    prior?.Invoke(services);
                    services.AddSingleton<IRevIncludePageResolver>(resolver);
                };
            }
        });
}

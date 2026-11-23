using System.Net;
using System.Text;
using System.Text.Json;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Budgets;
using Ignixa.Federation.Tests.Integration.Continuations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// The public HTTP slice of a distributed depth-one forward chain, driven through the real <c>Program</c> with a scripted host
/// strategy. The strategy stands in for Core's join, so these tests pin what the host does around it: authorization and shape
/// precedence, the one budget-bound client, canonical owner streams, the ordinary pager, and replay that is pure and client-free.
/// </summary>
public sealed class PrimarySearchDistributedForwardTests
{
    private const string Chain = "subject:Patient.given=Eli";
    private const string Paged = Chain + "&_count=1";
    private const string EastObservation = "https://fhir-east.example.org/Observation/obs-dup";
    private const string WestCrossReference = "https://fhir-west.example.org/Observation/obs-dup-xref";
    private const string EastPatient = "https://fhir-east.example.org/Patient/dup";
    private const string EastCursorTarget = "https://fhir-east.example.org/Observation?_getpages=opaque-east&_offset=1&_count=1";

    private static readonly string EastRewrite = Subject("Patient/dup", EastPatient);
    private static readonly string WestRewrite = Subject(EastPatient);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoOwnersWithTheSameIdPageTheirOwnCanonicalRewritesAndReplayDoesNoWitnessIo(bool post)
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy);

        SearchResult first = await SendAsync(host, post, Paged);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Entries.ShouldBe([(EastObservation, Body("east", "obs-dup"))]);
        first.Link("self").ShouldBe(SearchWorld.PublicBase + "/Observation?" + Paged);
        first.Json.TryGetProperty("total", out _).ShouldBeFalse();
        host.Shards.Views.Count.ShouldBe(1);
        ForwardResolveCall resolve = strategy.Resolves.ShouldHaveSingleItem();
        resolve.Capabilities.ShouldBeSameAs(host.Shards.Views.Single());
        resolve.Readable.ShouldBe([ResourceType.Parse("Observation"), ResourceType.Parse("Patient")], ignoreOrder: true);
        resolve.FetchAllowance.ShouldBe(resolve.Limits.BackendCallsPerShard * 2);
        resolve.PayloadByteAllowance.ShouldBe(resolve.Limits.MaxResponseBytes);
        host.Shards.Calls.Where(c => c.Parameters == "given=Eli").Select(c => c.Shard).Order(StringComparer.Ordinal)
            .ShouldBe(["east", "west"]);
        ShardCall east = host.Shards.Calls.Single(IsOwnerCall);
        (east.Shard, east.Method, east.Parameters, east.PageSize).ShouldBe(("east", ShardSearchMethod.PostSearch, EastRewrite, 1));
        string next = first.Link("next").ShouldNotBeNull();
        int calls = host.Shards.Calls.Count;

        SearchResult second = await host.FollowAsync(next);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Entries.ShouldBe([(WestCrossReference, Body("west", "obs-dup-xref"))]);
        second.Link("next").ShouldBeNull();
        host.Shards.Views.Count.ShouldBe(2);
        strategy.Resolves.Count.ShouldBe(1);
        strategy.Rebuilds.ShouldHaveSingleItem();
        ShardCall west = host.Shards.Calls.Skip(calls).ShouldHaveSingleItem();
        (west.Shard, west.Method, west.Parameters, west.PageSize).ShouldBe(("west", ShardSearchMethod.Get, WestRewrite, 1));
    }

    /// <summary>
    /// Pins only the HTTP glue for one certified kept predicate: the chain alone is covered elsewhere. The kept <c>code</c> term comes
    /// first, so the original order is observable. Over POST it carries a raw wire escape, which the form body forwards verbatim to every
    /// owner request, initial and replayed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACertifiedKeptPredicateSurvivesBothOwnersInitialAndReplayedRequestsInOrder(bool post)
    {
        string kept = post ? "code=oracle%2Dcode" : "code=oracle-code";
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy);

        SearchResult first = await SendAsync(host, post, kept + "&" + Paged);

        first.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body);
        first.Entries.ShouldBe([(EastObservation, Body("east", "obs-dup"))]);
        ShardCall east = host.Shards.Calls.Single(IsOwnerCall);
        (east.Shard, east.Method, east.Parameters, east.PageSize).ShouldBe(("east", ShardSearchMethod.PostSearch, kept + "&" + EastRewrite, 1));
        int calls = host.Shards.Calls.Count;

        SearchResult second = await host.FollowAsync(first.Link("next").ShouldNotBeNull());

        second.StatusCode.ShouldBe(HttpStatusCode.OK, second.Body);
        second.Entries.ShouldBe([(WestCrossReference, Body("west", "obs-dup-xref"))]);
        ShardCall west = host.Shards.Calls.Skip(calls).ShouldHaveSingleItem();
        (west.Shard, west.Method, west.Parameters, west.PageSize).ShouldBe(("west", ShardSearchMethod.Get, kept + "&" + WestRewrite, 1));
        host.Shards.Calls.Where(c => !IsOwnerCall(c)).ShouldAllBe(c => c.Parameters == "given=Eli");
        strategy.Resolves.ShouldHaveSingleItem();
        strategy.Rebuilds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task NoStrategyRefusesBeforeAnyClient()
    {
        using SearchHost host = Start(strategy: null);

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetTypeGrantIsRequiredBeforeTheStrategyOrAnyClient(bool post)
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy);
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole], ObjectId = SearchWorld.CallerObjectId });

        SearchResult refused = post
            ? await host.PostFormAsync("/Observation/_search", Paged, token)
            : await host.GetAsync("/Observation?" + Paged, token);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refused.IssueCode.ShouldBe("forbidden");
        strategy.Resolves.ShouldBeEmpty();
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("&_count=1&_total=accurate")]
    [InlineData("&_count=0")]
    [InlineData("&_summary=count")]
    [InlineData("&_count=1&subject:Patient.given=Eve")]
    [InlineData("&_count=1&_sort=_lastUpdated")]
    public async Task UnsupportedCombinationsRefuseBeforeTheStrategyOrAnyClient(string extra)
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy);

        SearchResult refused = await host.GetAsync("/Observation?" + Chain + extra);

        // The planner's existing refusals stand: this fixture certifies no count, total or sort shape for the hop.
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, refused.Body);
        strategy.Resolves.ShouldBeEmpty();
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoWitnessReturnsAnEmptySearchsetWithOnlyASelfLinkAndNoOwnerCall()
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy);
        const string query = "subject:Patient.given=Nobody&_count=1";

        SearchResult result = await host.GetAsync("/Observation?" + query);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.ShouldBeEmpty();
        result.Link("self").ShouldBe(SearchWorld.PublicBase + "/Observation?" + query);
        result.Link("next").ShouldBeNull();
        result.Json.TryGetProperty("total", out _).ShouldBeFalse();
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "given=Nobody");
        host.Shards.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task InsufficientEvidenceIsNotSupportedWithoutAnOwnerCall()
    {
        var strategy = new ScriptedDistributedForward { ResolveScript = _ => Task.FromResult<(IReadOnlyList<ShardSearchRequest>, int)?>(null) };
        using SearchHost host = Start(strategy);

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe("not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task HostFitAuthorityRefusesTheCanonicalStreamsBeforeAnyOwnerCall()
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy, s => s.Shards.RequestFits = r => r.Type.Value != "Observation");

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe("too-costly");
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "given=Eli");
    }

    [Fact]
    public async Task StrategyBudgetExhaustionIsTooCostly()
    {
        var strategy = new ScriptedDistributedForward { ResolveScript = _ => throw new FetchBudgetExhaustedException(1) };
        using SearchHost host = Start(strategy);

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe("too-costly");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task OwnerCallsShareTheAllowanceTheStrategyLeft()
    {
        var strategy = new ScriptedDistributedForward
        {
            ResolveScript = async call => (await ScriptedDistributedForward.DiscoverAsync(call))!.Value with { RemainingFetches = 1 },
        };
        using SearchHost host = Start(strategy);

        SearchResult refused = await host.GetAsync("/Observation?" + Chain + "&_count=2");

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe("too-costly");
        refused.Entries.ShouldBeEmpty();
        host.Shards.Calls.Count(IsOwnerCall).ShouldBe(1);
    }

    [Fact]
    public async Task WitnessTransportFailureNeverLeaksAPartialPage()
    {
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy, s => s.Shards.FailingShard = "west");

        SearchResult result = await host.GetAsync("/Observation?" + Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.Body.ShouldContain("p9xm");
        result.Entries.ShouldBeEmpty();
        result.Link("next").ShouldBeNull();
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "given=Eli");
    }

    [Fact]
    public async Task NeededOwnerFailureOnThePrimaryPageNeverYieldsPartialEntries()
    {
        var strategy = new ScriptedDistributedForward { ResolveScript = Canonical };
        using SearchHost host = Start(strategy, s => s.Shards.FailingShard = "west");

        SearchResult result = await host.GetAsync("/Observation?" + Chain + "&_count=2");

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.Entries.ShouldBeEmpty();
        result.Link("next").ShouldBeNull();
    }

    [Fact]
    public async Task LiveDeadlineWinsOverAWitnessTransportFailure()
    {
        var time = new ManualTimeProvider();
        var strategy = new ScriptedDistributedForward();
        using SearchHost host = Start(strategy, s =>
        {
            s.Time = time;
            s.Shards.FailingShard = "east";
            s.Shards.DuringCall = () => time.Advance(TimeSpan.FromMinutes(1));
        });

        SearchResult result = await host.GetAsync("/Observation?" + Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        result.IssueCode.ShouldBe("timeout");
        result.Body.ShouldNotContain("k7q2");
    }

    /// <summary>
    /// A strategy's own <see cref="TimeoutException"/> is a live-deadline signal: a gateway timeout with no owner call. The budget still
    /// speaks first: a request-budget breach the strategy swallowed stays latched and wins as too costly.
    /// </summary>
    [Theory]
    [InlineData(false, HttpStatusCode.GatewayTimeout, "timeout")]
    [InlineData(true, HttpStatusCode.BadRequest, "too-costly")]
    public async Task AStrategyTimeoutIsAGatewayTimeoutUnlessARecordedBudgetBreachComesFirst(bool breach, HttpStatusCode status, string code)
    {
        var strategy = new ScriptedDistributedForward
        {
            ResolveScript = async call =>
            {
                await ScriptedDistributedForward.DiscoverAsync(call);
                try
                {
                    // One more witness-shaped call: it fits the default pool, but exceeds the two-call pool of the breach row.
                    await call.Client.SearchAsync(new ShardSearchRequest(call.Capabilities.Registry.Shards[0].Id, ResourceType.Parse("Patient"),
                        [new ParameterOccurrence("given", "Eli")], 100, ShardSearchMethod.Get), call.Token);
                }
                catch (RequestTooCostlyException)
                {
                }

                throw new TimeoutException();
            },
        };
        using SearchHost host = Start(strategy, s =>
        {
            if (breach)
            {
                s.Settings["Federation:Limits:BackendCallsPerShard"] = "1";
            }
        });

        SearchResult result = await host.GetAsync("/Observation?" + Paged);

        result.StatusCode.ShouldBe(status, result.Body);
        result.IssueCode.ShouldBe(code);
        result.Entries.ShouldBeEmpty();
        result.Link("next").ShouldBeNull();
        host.Shards.Calls.Count(IsOwnerCall).ShouldBe(0);
    }

    /// <summary>
    /// Witness discovery and owner paging are one request on one budget. The strategy here understates its use and claims the whole
    /// allowance remains, so only the host's shared physical count can refuse the owners; the other row proves an exact fit passes.
    /// </summary>
    [Theory]
    [InlineData(2, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.BadRequest)]
    public async Task WitnessAndOwnerSendsShareOnePhysicalCallBudgetWhateverTheStrategyReports(int callsPerShard, HttpStatusCode status)
    {
        var strategy = new ScriptedDistributedForward
        {
            ResolveScript = async call => (await ScriptedDistributedForward.DiscoverAsync(call))!.Value with { RemainingFetches = call.FetchAllowance },
        };
        using SearchHost host = Start(strategy, s =>
            s.Settings["Federation:Limits:BackendCallsPerShard"] = callsPerShard.ToString(System.Globalization.CultureInfo.InvariantCulture));

        SearchResult result = await host.GetAsync("/Observation?" + Chain + "&_count=2");

        result.StatusCode.ShouldBe(status, result.Body);
        host.Shards.Calls.Count.ShouldBe(callsPerShard * 2);
        if (status == HttpStatusCode.OK)
        {
            result.Entries.Count.ShouldBe(2);
        }
        else
        {
            result.IssueCode.ShouldBe("too-costly");
            result.Entries.ShouldBeEmpty();
            host.Shards.Calls.Count(IsOwnerCall).ShouldBe(0);
        }
    }

    /// <summary>
    /// Witness bytes and owner bytes are each within the limit alone but not together: the shared response-byte budget, not a
    /// per-phase one, decides. The other row sits exactly on the limit and passes.
    /// </summary>
    [Theory]
    [InlineData(0, HttpStatusCode.OK)]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    public async Task WitnessAndOwnerResponsesShareOneByteBudget(int adjustment, HttpStatusCode status)
    {
        long witness = SearchWorld.Slice("east", "Patient", "dup").Length;
        long owners = SearchWorld.Slice("east", "Observation", "obs-dup").Length + SearchWorld.Slice("west", "Observation", "obs-dup-xref").Length;
        long limit = witness + owners + adjustment;
        limit.ShouldBeGreaterThanOrEqualTo(owners);
        using SearchHost host = Start(new ScriptedDistributedForward(), s =>
            s.Settings["Federation:Limits:MaxResponseBytes"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        SearchResult result = await host.GetAsync("/Observation?" + Chain + "&_count=2");

        result.StatusCode.ShouldBe(status, result.Body);
        if (status == HttpStatusCode.OK)
        {
            result.Entries.Count.ShouldBe(2);
        }
        else
        {
            result.IssueCode.ShouldBe("too-costly");
            result.Entries.ShouldBeEmpty();
        }
    }

    /// <summary>The host fit authority judges what an owner is actually sent: the plan's count clamped to that owner's maximum.</summary>
    [Fact]
    public async Task HostFitJudgesTheClampedCountNotThePlanCount()
    {
        SearchCapabilities view = Capabilities(EastRegistry(maxCount: 1, NextLinkRedemption.FollowGet));
        using SearchHost host = Start(new ScriptedDistributedForward(), s =>
        {
            s.Capabilities = view;
            s.Shards.RequestFits = r => r.Shard.Value != "east" || r.PageSize <= 1;
        });

        SearchResult result = await host.GetAsync("/Observation?" + Chain + "&_count=2");

        result.StatusCode.ShouldBe(HttpStatusCode.OK, result.Body);
        result.Entries.Count.ShouldBe(2);
        host.Shards.Calls.Single(c => IsOwnerCall(c) && c.Shard == "east").PageSize.ShouldBe(1);
    }

    [Theory]
    [InlineData("reversed registry order")]
    [InlineData("missing owner")]
    [InlineData("wrong page size")]
    [InlineData("wrong type")]
    [InlineData("post to a get-only owner")]
    [InlineData("extra parameter")]
    [InlineData("raw chain fallback")]
    [InlineData("claimed remaining fetches above the allowance")]
    public async Task StreamsOutsideTheCanonicalBoundaryAreRefusedBeforeAnyOwnerCall(string violation)
    {
        var strategy = new ScriptedDistributedForward { ResolveScript = async call => Violate(violation, (await Canonical(call))!.Value, call) };
        using SearchHost host = Start(strategy);

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.InternalServerError, violation);
        refused.Entries.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplayPinsTheSourceReferenceTheTargetTerminalAndEveryShardButNotTheUnconsultedTargetId()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost host = Start(new ScriptedDistributedForward(), s => s.Protection = ring.CreateProvider());
        SearchResult first = await host.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var consulted = new HashSet<(ContinuationDependencyGroup, string)>();
        SearchCapabilities view = Capabilities();

        Protector(ring).Redeem(Token(first.Link("next").ShouldNotBeNull()), Binding(), (group, key) =>
        {
            consulted.Add((group, key));
            return Revision(view, group, key);
        }).Outcome.ShouldBe(ContinuationRedemptionOutcome.Redeemed);

        consulted.ShouldBe(new HashSet<(ContinuationDependencyGroup, string)>
        {
            (ContinuationDependencyGroup.Shard, "east"), (ContinuationDependencyGroup.Shard, "west"),
            (ContinuationDependencyGroup.Definition, "Observation.subject"), (ContinuationDependencyGroup.Definition, "Patient.given"),
        }, ignoreOrder: true);
    }

    [Fact]
    public async Task ReplayRechecksTheTargetTypeGrantBeforeTheStrategyOrAnyClient()
    {
        using var ring = new IsolatedKeyRing();
        var narrowStrategy = new ScriptedDistributedForward();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        using SearchHost narrowed = Start(narrowStrategy, s =>
        {
            s.Protection = ring.CreateProvider();
            s.Settings[$"Federation:Host:Roles:{SearchWorld.ReaderRole}:0"] = "Observation";
            s.Settings.Remove($"Federation:Host:Roles:{SearchWorld.ReaderRole}:1");
        });

        SearchResult refused = await narrowed.FollowAsync(next);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refused.IssueCode.ShouldBe("forbidden");
        narrowStrategy.Rebuilds.ShouldBeEmpty();
        narrowed.Shards.Views.ShouldBeEmpty();
        narrowed.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplayOnAHostWithoutTheStrategyIsNotImplementedBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        using SearchHost bare = Start(strategy: null, s => s.Protection = ring.CreateProvider());

        SearchResult refused = await bare.FollowAsync(next);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        bare.Shards.Views.ShouldBeEmpty();
        bare.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplayWithoutTheDistributedProofRestartsBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();
        var strategy = new ScriptedDistributedForward();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        using SearchHost drifted = Start(strategy, s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = SearchWorld.Capabilities(patientGiven: true);
        });

        await AssertRestartWithoutClientAsync(drifted, next);
        strategy.Rebuilds.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APassthroughTokenOnAHostThatIsNowDistributedRestartsBeforeAnyClient(bool withStrategy)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = SearchHost.Start(setup =>
        {
            setup.Protection = ring.CreateProvider();
            setup.Capabilities = SearchWorld.Capabilities(forwardChain: true, patientGiven: true);
            setup.Settings["Federation:ChainResolution:Passthrough:0"] = "Observation.subject:Patient";
        });
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var strategy = new ScriptedDistributedForward();
        using SearchHost redeemer = Start(withStrategy ? strategy : null, s => s.Protection = ring.CreateProvider());

        await AssertRestartWithoutClientAsync(redeemer, first.Link("next").ShouldNotBeNull());
        strategy.Resolves.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReplayRefusesAnUnrebuildableOrMismatchedPlanBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        var strategy = new ScriptedDistributedForward { RebuildScript = _ => null };
        using SearchHost refusing = Start(strategy, s => s.Protection = ring.CreateProvider());
        await AssertRestartWithoutClientAsync(refusing, next);

        var mismatch = new ScriptedDistributedForward
        {
            RebuildScript = call => ScriptedDistributedForward.Streams(call.Plan, call.Capabilities.Registry,
                [(call.Capabilities.Registry.Shards[1], "dup")]),
        };
        using SearchHost mismatching = Start(mismatch, s => s.Protection = ring.CreateProvider());
        await AssertRestartWithoutClientAsync(mismatching, next);
        strategy.Rebuilds.ShouldHaveSingleItem();
        mismatch.Rebuilds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ReplayRefusesWhenTheHostFitAuthorityNoLongerAcceptsTheStreamsBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        using SearchHost tightened = Start(new ScriptedDistributedForward(), s =>
        {
            s.Protection = ring.CreateProvider();
            s.Shards.RequestFits = _ => false;
        });

        await AssertRestartWithoutClientAsync(tightened, next);
    }

    [Fact]
    public async Task RegistryGrowthRestartsEvenWhenTheStrategyReturnsTheSavedStreams()
    {
        using var ring = new IsolatedKeyRing();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        using SearchHost grown = Start(new ScriptedDistributedForward { RebuildScript = call => call.Saved }, s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = SearchWorld.Capabilities(distributedForward: true, patientGiven: true,
                registry: SearchWorld.ReverseRegistry(extraWitnessShard: true));
            foreach (var (key, value) in SearchWorld.Settings(extraWitnessShard: true))
            {
                s.Settings[key] = value;
            }
        });

        await AssertRestartWithoutClientAsync(grown, next);
    }

    [Fact]
    public async Task ATrustingRebuildCannotReplayAStreamWithAnOwnerIncompatibleVerb()
    {
        using var ring = new IsolatedKeyRing();
        (SearchHost issuer, string next) = await IssueAsync(ring);
        using SearchHost _ = issuer;
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, next);
        ShardSearchRequest[] streams = [.. saved.Streams];
        streams[1] = new ShardSearchRequest(streams[1].Shard, streams[1].Type, streams[1].Parameters, streams[1].PageSize, ShardSearchMethod.PostSearch);
        string forged = IssueBookmark(protector, issued, frame, codec, new UnsortedContinuationSnapshot(streams, saved.PageSize, saved.Continuation));
        using SearchHost trusting = Start(new ScriptedDistributedForward { RebuildScript = call => call.Saved },
            s => s.Protection = ring.CreateProvider());

        await AssertRestartWithoutClientAsync(trusting, forged);
    }

    [Fact]
    public async Task ASignedSearchReplayContinuesFromItsAnchorWithNoNewDiscovery()
    {
        using var ring = new IsolatedKeyRing();
        SearchCapabilities view = Capabilities(EastRegistry(maxCount: 100, NextLinkRedemption.FollowGet));
        string token = await IssueSearchReplayAsync(ring, view);
        var strategy = new ScriptedDistributedForward();
        using SearchHost redeemer = Start(strategy, s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));

        SearchResult result = await redeemer.GetAsync("/Observation?ct=" + token);

        result.StatusCode.ShouldBe(HttpStatusCode.OK, result.Body);
        result.Entries.ShouldBe([(WestCrossReference, Body("west", "obs-dup-xref"))]);
        result.Link("next").ShouldBeNull();
        strategy.Resolves.ShouldBeEmpty();
        strategy.Rebuilds.ShouldHaveSingleItem();
        redeemer.Shards.Calls.Select(c => (c.Shard, c.Method, c.Parameters, c.PageSize)).Order().ShouldBe(
        [
            ("east", ShardSearchMethod.PostSearch, EastRewrite, 2),
            ("west", ShardSearchMethod.Get, WestRewrite, 2),
        ]);
    }

    [Fact]
    public async Task ASignedSearchReplayLargerThanTheOwnersCurrentMaxCountRestartsBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();

        // The same snapshot as the passing replay above, issued under and redeemed on a registry whose east maximum is now 1:
        // revisions match, so only the replay-profile check can refuse it.
        SearchCapabilities view = Capabilities(EastRegistry(maxCount: 1, NextLinkRedemption.FollowGet));
        string token = await IssueSearchReplayAsync(ring, view);
        var strategy = new ScriptedDistributedForward();
        using SearchHost redeemer = Start(strategy, s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));

        await AssertRestartWithoutClientAsync(redeemer, token);
        strategy.Rebuilds.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ACursorOfTheOwnersCurrentRedemptionPolicyContinuesTheSavedPostStream()
    {
        using var ring = new IsolatedKeyRing();
        SearchCapabilities view = Capabilities(EastRegistry(maxCount: 100, NextLinkRedemption.FollowGet));
        string token = await IssuePostCursorReplayAsync(ring, view);
        var strategy = new ScriptedDistributedForward();
        using SearchHost redeemer = Start(strategy, s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));

        SearchResult result = await redeemer.GetAsync("/Observation?ct=" + token);

        result.StatusCode.ShouldBe(HttpStatusCode.OK, result.Body);
        result.Entries.ShouldBe([(WestCrossReference, Body("west", "obs-dup-xref"))]);
        strategy.Resolves.ShouldBeEmpty();
        redeemer.Shards.Calls.Select(c => (c.Shard, c.Method, c.Parameters, c.PageSize, c.Target)).Order().ShouldBe(
        [
            ("east", null, null, 0, EastCursorTarget),
            ("west", ShardSearchMethod.Get, WestRewrite, 1, null),
        ]);
    }

    [Fact]
    public async Task APostStreamCursorWhoseRedemptionNoLongerMatchesTheOwnersPolicyRestartsBeforeAnyClient()
    {
        using var ring = new IsolatedKeyRing();

        // The saved cursor is a structurally valid FollowGet cursor of a POST stream. Under the repost policy the owner's POST
        // stream must redeem by re-posting, so only the replay-profile check can refuse it. The registry revision is unchanged.
        SearchCapabilities view = Capabilities(EastRegistry(maxCount: 100, NextLinkRedemption.RepostToSearch));
        string token = await IssuePostCursorReplayAsync(ring, view);
        var strategy = new ScriptedDistributedForward();
        using SearchHost redeemer = Start(strategy, s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));

        await AssertRestartWithoutClientAsync(redeemer, token);
        strategy.Rebuilds.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Issues a real, signed bookmark whose snapshot is replaying the east stream at <c>_count=2</c> from <c>Observation/obs-dup</c>,
    /// under the dependency revisions of <paramref name="view"/>. The page size 2 is the saved replay count too.
    /// </summary>
    private static async Task<string> IssueSearchReplayAsync(IsolatedKeyRing ring, SearchCapabilities view)
    {
        using SearchHost issuer = Start(new ScriptedDistributedForward(), s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body);
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, first.Link("next").ShouldNotBeNull(), view);
        ShardSearchRequest[] streams = [.. saved.Streams.Select(s => new ShardSearchRequest(s.Shard, s.Type, s.Parameters, 2, s.Method))];
        var anchor = new ResourceKey(streams[0].Shard, streams[0].Type, ResourceId.Parse("obs-dup"));
        return IssueBookmark(protector, issued, frame, codec,
            new UnsortedContinuationSnapshot(streams, 2, UnsortedContinuation.AtSearch(streams[0], anchor)), Chain + "&_count=2");
    }

    /// <summary>Issues a real, signed bookmark that continues the east POST stream from an opaque cursor past its only observation.</summary>
    private static async Task<string> IssuePostCursorReplayAsync(IsolatedKeyRing ring, SearchCapabilities view)
    {
        using SearchHost issuer = Start(new ScriptedDistributedForward(), s => (s.Protection, s.Capabilities) = (ring.CreateProvider(), view));
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body);
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, first.Link("next").ShouldNotBeNull(), view);
        saved.Streams[0].Method.ShouldBe(ShardSearchMethod.PostSearch);
        var cursor = new ShardCursor(saved.Streams[0].Shard, saved.Streams[0].Type, EastCursorTarget, NextLinkRedemption.FollowGet);
        return IssueBookmark(protector, issued, frame, codec,
            new UnsortedContinuationSnapshot(saved.Streams, saved.PageSize, UnsortedContinuation.AtCursor(cursor, null, CursorCycleState.Initial)));
    }

    private static ShardRegistry EastRegistry(int maxCount, NextLinkRedemption redemption) =>
        SearchWorld.ReverseRegistry(extraWitnessShard: false,
            new BackendProfile(SearchWorld.PostCapable.Name, SupportsPostSearch: true, maxCount, redemption, null));

    private static SearchCapabilities Capabilities(ShardRegistry registry) =>
        SearchWorld.Capabilities(distributedForward: true, patientGiven: true, registry: registry);

    private static async Task<(SearchHost Issuer, string Next)> IssueAsync(IsolatedKeyRing ring)
    {
        SearchHost issuer = Start(new ScriptedDistributedForward(), s => s.Protection = ring.CreateProvider());
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (issuer, first.Link("next").ShouldNotBeNull());
    }

    private static async Task AssertRestartWithoutClientAsync(SearchHost host, string next)
    {
        SearchResult refused = next.StartsWith("http", StringComparison.Ordinal)
            ? await host.FollowAsync(next)
            : await host.GetAsync("/Observation?ct=" + next);
        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        refused.IssueCode.ShouldBe("not-found");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    /// <summary>Canonical streams with no I/O: east witnesses <c>Patient/dup</c>.</summary>
    private static Task<(IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)?> Canonical(ForwardResolveCall call) =>
        Task.FromResult<(IReadOnlyList<ShardSearchRequest>, int)?>((
            ScriptedDistributedForward.Streams(call.Plan, call.Capabilities.Registry, [(call.Capabilities.Registry.Shards[0], "dup")]),
            call.FetchAllowance));

    private static (IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)? Violate(
        string violation, (IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches) good, ForwardResolveCall call)
    {
        ShardSearchRequest east = good.Streams[0];
        ShardSearchRequest west = good.Streams[1];
        ShardSearchRequest With(ShardSearchRequest r, ResourceType? type = null, int? pageSize = null, ShardSearchMethod? method = null,
            IEnumerable<ParameterOccurrence>? parameters = null) =>
            new(r.Shard, type ?? r.Type, parameters ?? r.Parameters, pageSize ?? r.PageSize, method ?? r.Method);
        return violation switch
        {
            "reversed registry order" => ([west, east], good.RemainingFetches),
            "missing owner" => ([east], good.RemainingFetches),
            "wrong page size" => ([east, With(west, pageSize: 2)], good.RemainingFetches),
            "wrong type" => ([east, With(west, type: ResourceType.Parse("Patient"))], good.RemainingFetches),
            "post to a get-only owner" => ([east, With(west, method: ShardSearchMethod.PostSearch)], good.RemainingFetches),
            "extra parameter" => ([east, With(west, parameters: [.. west.Parameters, new ParameterOccurrence("code", "x")])], good.RemainingFetches),
            "raw chain fallback" => ([east, With(west, parameters: [new ParameterOccurrence("subject%3APatient.given", "Eli")])], good.RemainingFetches),
            _ => (good.Streams, call.FetchAllowance + 1),
        };
    }

    private static bool IsOwnerCall(ShardCall call) =>
        call.Parameters?.Split('&').Any(p => p.StartsWith("subject=", StringComparison.Ordinal)) == true;

    private static string Subject(params string[] forms) => "subject=" + Uri.EscapeDataString(string.Join(',', forms));

    /// <summary>Models a backend that honours the request: the witness <c>given</c> and the generated <c>subject</c> list.</summary>
    private static bool Matches(ShardSearchRequest request, SearchWorld.SeedResource resource)
    {
        using JsonDocument doc = JsonDocument.Parse(resource.Json);
        foreach (ParameterOccurrence p in request.Parameters)
        {
            string key = WebUtility.UrlDecode(p.Key);
            string value = WebUtility.UrlDecode(p.Value);
            bool ok = (request.Type.Value, key) switch
            {
                ("Patient", "given") => doc.RootElement.TryGetProperty("name", out JsonElement names)
                    && names.EnumerateArray().Any(n => n.TryGetProperty("given", out JsonElement given)
                        && given.EnumerateArray().Any(g => g.GetString() == value)),
                ("Observation", "subject") => doc.RootElement.TryGetProperty("subject", out JsonElement subject)
                    && subject.TryGetProperty("reference", out JsonElement reference)
                    && value.Split(',').Contains(reference.GetString(), StringComparer.Ordinal),
                ("Observation", "code") => doc.RootElement.TryGetProperty("code", out JsonElement code)
                    && code.TryGetProperty("coding", out JsonElement codings)
                    && codings.EnumerateArray().Any(c => c.TryGetProperty("code", out JsonElement id) && id.GetString() == value),
                _ => true,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static SearchHost Start(ScriptedDistributedForward? strategy, Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = Capabilities();
            setup.Settings.Remove("Federation:ChainResolution:Passthrough:0");
            setup.Shards.Matches = Matches;
            configure?.Invoke(setup);
            if (strategy is not null)
            {
                setup.ConfigureServices = services => services.AddSingleton<IDistributedForwardSearch>(strategy);
            }
        });

    private static SearchCapabilities Capabilities() => SearchWorld.Capabilities(distributedForward: true, patientGiven: true);

    private static Task<SearchResult> SendAsync(SearchHost host, bool post, string query) =>
        post ? host.PostFormAsync("/Observation/_search", query) : host.GetAsync("/Observation?" + query);

    private static string Body(string shard, string id) => Encoding.UTF8.GetString(SearchWorld.Slice(shard, "Observation", id));

    private static ContinuationProtector Protector(IsolatedKeyRing ring) =>
        new(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);

    private static string Token(string next) => new Uri(next).Query["?ct=".Length..];

    private static ContinuationBinding Binding() =>
        ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject",
            SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]);

    private static string? Revision(SearchCapabilities view, ContinuationDependencyGroup group, string key) =>
        group == ContinuationDependencyGroup.Shard
            ? view.Registry.ComputeVersion([ShardId.Parse(key)])
            : key.Split('.', 2) is [string type, string code]
                ? view.GetResolvedDefinitionRevision(ResourceType.Parse(type), code)
                : null;

    private static (ContinuationProtector Protector, RedeemedContinuation Issued, SearchContinuationFrame Frame,
        UnsortedSnapshotCodec Codec, UnsortedContinuationSnapshot Saved) ReadBookmark(IsolatedKeyRing ring, string next, SearchCapabilities? currentView = null)
    {
        SearchCapabilities view = currentView ?? Capabilities();
        ContinuationProtector protector = Protector(ring);
        RedeemedContinuation issued = protector.Redeem(Token(next), Binding(), (group, key) => Revision(view, group, key))
            .Continuation.ShouldNotBeNull();
        SearchContinuationFrame.TryRead(issued.Snapshot.AsSpan(), out SearchContinuationFrame? frame)
            .ShouldBe(UnsortedSnapshotOutcome.Success);
        var codec = new UnsortedSnapshotCodec(new TokenOptions().MaxInlineTokenBytes, view.Registry);
        codec.Decode(frame!.Snapshot, out UnsortedContinuationSnapshot? saved).ShouldBe(UnsortedSnapshotOutcome.Success);
        return (protector, issued, frame, codec, saved.ShouldNotBeNull());
    }

    private static string IssueBookmark(ContinuationProtector protector, RedeemedContinuation issued, SearchContinuationFrame frame,
        UnsortedSnapshotCodec codec, UnsortedContinuationSnapshot snapshot, string? rawQuery = null)
    {
        codec.Encode(snapshot, out byte[]? encoded).ShouldBe(UnsortedSnapshotOutcome.Success);
        return protector.IssueSuccessor(issued, new SearchContinuationFrame(frame.Post, rawQuery ?? frame.RawQuery, encoded.ShouldNotBeNull()).ToArray())
            .InlineToken.ShouldNotBeNull();
    }
}

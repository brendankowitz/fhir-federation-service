using System.Net;
using System.Text;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution.Counts;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Continuations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// Depth-one forward chains the operator lists under <c>Federation:ChainResolution:Passthrough</c> and every shard proves
/// natively: each shard joins locally, so the page is the ordinary unsorted pager over the raw occurrences.
/// </summary>
public sealed class PrimarySearchForwardChainTests
{
    private const string PassthroughKey = "Federation:ChainResolution:Passthrough:0";
    private const string Path = "Observation.subject:Patient";
    private const string Chain = "subject:Patient.name:exact=Orchid";
    private const string Paged = Chain + "&_count=1";
    private const string EastObservation = "https://fhir-east.example.org/Observation/obs-dup";
    private const string WestObservation = "https://fhir-west.example.org/Observation/obs-dup";
    private const string WestCrossReference = "https://fhir-west.example.org/Observation/obs-dup-xref";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothMethodsWalkTheLocalPathAcrossOwnersKeepingTheRawOccurrencesAndTheOwnersVerbs(bool post)
    {
        using SearchHost host = Start();
        const string query = "code=oracle-code&subject%3APatient.name=Or%20chid&subject:Patient.name:exact=Orchid&_count=1";
        const string wire = "code=oracle-code&subject%3APatient.name=Or%20chid&subject:Patient.name:exact=Orchid";

        SearchResult first = await SendAsync(host, post, query);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Entries.ShouldBe([(EastObservation, Body("east", "obs-dup"))]);
        first.Link("self").ShouldBe(SearchWorld.PublicBase + "/Observation?" + query);
        first.Json.TryGetProperty("total", out _).ShouldBeFalse();
        string next = first.Link("next").ShouldNotBeNull();
        next.ShouldStartWith(SearchWorld.PublicBase + "/Observation?ct=");
        host.Shards.Views.Count.ShouldBe(1);
        ShardCall east = host.Shards.Calls.ShouldHaveSingleItem();
        (east.Shard, east.Method, east.Parameters).ShouldBe(("east", post ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get, wire));

        SearchResult second = await host.FollowAsync(next);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Entries.ShouldBe([(WestObservation, Body("west", "obs-dup"))]);
        ShardCall west = host.Shards.Calls[^1];
        (west.Shard, west.Method, west.Parameters).ShouldBe(("west", ShardSearchMethod.Get, wire));

        SearchResult third = await host.FollowAsync(second.Link("next").ShouldNotBeNull());

        third.StatusCode.ShouldBe(HttpStatusCode.OK);
        third.Entries.ShouldBe([(WestCrossReference, Body("west", "obs-dup-xref"))]);
        third.Link("next").ShouldBeNull();
        host.Shards.Calls[^1].Target.ShouldNotBeNull();
        host.Shards.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task FormPostJoinsUrlThenBodyOccurrencesInOrder()
    {
        using SearchHost host = Start();

        SearchResult first = await host.PostFormAsync("/Observation/_search?code=oracle-code",
            "subject%3APatient.name=Or%63hid&subject:Patient.family=Orchid&_count=1");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Link("self").ShouldBe(SearchWorld.PublicBase
            + "/Observation?code=oracle-code&subject%3APatient.name=Or%63hid&subject:Patient.family=Orchid&_count=1");
        ShardCall east = host.Shards.Calls.ShouldHaveSingleItem();
        (east.Method, east.Parameters).ShouldBe((ShardSearchMethod.PostSearch,
            "code=oracle-code&subject%3APatient.name=Or%63hid&subject:Patient.family=Orchid"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TargetTypeGrantIsRequiredBeforeAnyClientOrShardWork(bool post, bool distributed)
    {
        using SearchHost host = Start(s =>
        {
            if (distributed)
            {
                s.Capabilities = SearchWorld.Capabilities(distributedForward: true);
                s.Settings.Remove(PassthroughKey);
            }
        });
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole], ObjectId = SearchWorld.CallerObjectId });

        SearchResult refused = post
            ? await host.PostFormAsync("/Observation/_search", Paged, token)
            : await host.GetAsync("/Observation?" + Paged, token);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refused.IssueCode.ShouldBe("forbidden");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();

        // The same bearer may search the primary type itself: only the target type is missing.
        SearchResult primary = await host.GetAsync("/Observation?code=oracle-code&_count=1", token);
        primary.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedemptionRechecksTheTargetTypeGrantForTheSameCallerBeforeAnyClient(bool distributed)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(s => s.Protection = ring.CreateProvider());
        using SearchHost narrowed = Start(s =>
        {
            s.Protection = ring.CreateProvider();
            s.Settings[$"Federation:Host:Roles:{SearchWorld.ReaderRole}:0"] = "Observation";
            s.Settings.Remove($"Federation:Host:Roles:{SearchWorld.ReaderRole}:1");
            if (distributed)
            {
                s.Capabilities = SearchWorld.Capabilities(distributedForward: true);
                s.Settings.Remove(PassthroughKey);
            }
        });
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        SearchResult refused = await narrowed.FollowAsync(first.Link("next").ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        refused.IssueCode.ShouldBe("forbidden");
        narrowed.Shards.Views.ShouldBeEmpty();
        narrowed.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryChainOccurrencePinsTheSourceReferenceAndEachTargetTerminalDefinition()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost host = Start(s => s.Protection = ring.CreateProvider());
        SearchResult first = await host.GetAsync("/Observation?subject:Patient.name:exact=Orchid&subject:Patient.family=Orchid&_count=1");
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
            (ContinuationDependencyGroup.Definition, "Observation.subject"),
            (ContinuationDependencyGroup.Definition, "Patient.name"), (ContinuationDependencyGroup.Definition, "Patient.family"),
        }, ignoreOrder: true);
    }

    [Theory]
    [InlineData("Observation.subject", true)]
    [InlineData("Patient.name", true)]
    [InlineData("Observation.subject", false)]
    [InlineData("Patient.name", false)]
    public async Task EachPinIsComparedIndependentlyBeforeAnyClient(string definition, bool stale)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(s => s.Protection = ring.CreateProvider());
        using SearchHost redeemer = Start(s => s.Protection = ring.CreateProvider());
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        ContinuationProtector protector = Protector(ring);
        SearchCapabilities view = Capabilities();
        RedeemedContinuation issued = protector.Redeem(Token(next), Binding(), (group, key) => Revision(view, group, key))
            .Continuation.ShouldNotBeNull();
        var shards = view.Registry.Shards.ToDictionary(s => s.Id.Value, s => view.Registry.ComputeVersion([s.Id]), StringComparer.Ordinal);
        List<KeyValuePair<string, string>> definitions = [];
        foreach (string key in new[] { "Observation.subject", "Patient.name" })
        {
            string[] parts = key.Split('.');
            string revision = view.GetResolvedDefinitionRevision(ResourceType.Parse(parts[0]), parts[1]).ShouldNotBeNull();
            if (key != definition)
            {
                definitions.Add(KeyValuePair.Create(key, revision));
            }
            else if (stale)
            {
                definitions.Add(KeyValuePair.Create(key, "prior-expression-revision"));
            }
        }

        ContinuationIssue forged = protector.Issue(Binding(), ContinuationDependencySet.Create(shards, definitions), issued.Snapshot.AsSpan());
        SearchResult refused = await redeemer.GetAsync("/Observation?ct=" + forged.InlineToken.ShouldNotBeNull());

        // Drift of a pinned definition restarts the search; a manifest that omits one is not a manifest this plan produces.
        refused.StatusCode.ShouldBe(stale ? HttpStatusCode.Gone : HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe(stale ? "not-found" : "invalid");
        redeemer.Shards.Views.ShouldBeEmpty();
        redeemer.Shards.Calls.ShouldBeEmpty();
        (await redeemer.FollowAsync(next)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("path no longer listed", false, false)]
    [InlineData("distributed now planned", false, true)]
    [InlineData("native proof withdrawn", true, false)]
    public async Task ChangedOrUnsupportedModeIsRestartedBeforeAnyClient(string change, bool listed, bool nativeProof)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(s => s.Protection = ring.CreateProvider());
        using SearchHost redeemer = Start(s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = SearchWorld.Capabilities(forwardChain: nativeProof, distributedForward: true);
            if (!listed)
            {
                s.Settings.Remove(PassthroughKey);
            }
        });
        SearchResult first = await issuer.GetAsync("/Observation?" + Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, change);

        SearchResult refused = await redeemer.FollowAsync(first.Link("next").ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.Gone, change);
        refused.IssueCode.ShouldBe("not-found");
        redeemer.Shards.Views.ShouldBeEmpty();
        redeemer.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DistributedModeIsStillNotExecuted()
    {
        using SearchHost host = Start(s =>
        {
            s.Capabilities = SearchWorld.Capabilities(distributedForward: true);
            s.Settings.Remove(PassthroughKey);
        });

        SearchResult refused = await host.GetAsync("/Observation?" + Paged);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("&_total=accurate", HttpStatusCode.NotImplemented)]
    [InlineData("&_count=0", HttpStatusCode.NotImplemented)]
    [InlineData("&_summary=count", HttpStatusCode.NotImplemented)]
    [InlineData("&_sort=_lastUpdated", HttpStatusCode.BadRequest)]
    [InlineData("&_include=Observation:subject", HttpStatusCode.BadRequest)]
    public async Task CountTotalSortAndIncludeShapesAreStillRefusedWithoutAClient(string controls, HttpStatusCode status)
    {
        using SearchHost host = Start(s =>
            s.Capabilities = SearchWorld.Capabilities(forwardChain: true, exactCountShapes: ["Observation?subject:Patient.name:exact"]));

        SearchResult refused = await host.GetAsync("/Observation?" + Chain + controls);

        refused.StatusCode.ShouldBe(status);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ForwardMixedWithADistributedReverseChainIsStillRefusedWithoutAClient()
    {
        using SearchHost host = Start(s =>
        {
            s.Capabilities = SearchWorld.Capabilities(forwardChain: true, reverseChain: true, organizationForward: true,
                exactCountShapes: ["Patient?", "Patient?_id=,"]);
            s.Settings[PassthroughKey] = "Patient.organization:Organization";
            s.Settings[$"Federation:Host:Roles:{SearchWorld.ReaderRole}:2"] = "Organization";
            s.ConfigureServices = services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(
                s.Capabilities!.Registry.Shards.ToDictionary(shard => shard.Id, _ => CountDialect.CountZero));
        });

        SearchResult refused = await host.GetAsync("/Patient?organization.name=Acme&_has:Observation:subject:code=oracle-code&_count=1");

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    private static SearchHost Start(Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = Capabilities();
            setup.Settings[PassthroughKey] = Path;
            configure?.Invoke(setup);
        });

    private static SearchCapabilities Capabilities() => SearchWorld.Capabilities(forwardChain: true);

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
}

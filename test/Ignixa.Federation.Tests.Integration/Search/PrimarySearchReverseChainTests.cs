using System.Net;
using System.Text;
using Ignixa.Federation.Api.Continuations;
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
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

public sealed class PrimarySearchReverseChainTests
{
    private const string Has = "_has:Observation:subject:code=oracle-code";
    private const string Paged = Has + "&_count=1";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothMethodsPagePhysicalCollisionsWithoutRepeatingWitnessDiscovery(bool post)
    {
        using SearchHost host = Start();
        host.Shards.FilterGeneratedIds = true;

        SearchResult first = await SendAsync(host, post, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Entries.ShouldBe([(SearchWorld.EastDupUrl, Body("east"))]);
        first.Link("self").ShouldBe(SearchWorld.PublicBase + "/Patient?" + Paged);
        first.Json.TryGetProperty("total", out _).ShouldBeFalse();
        host.Shards.Views.Count.ShouldBe(1);
        host.Shards.Calls.Where(c => c.Parameters == "code=oracle-code").Select(c => c.Shard)
            .Order(StringComparer.Ordinal).ShouldBe(["east", "west"]);
        ShardCall east = host.Shards.Calls.Single(c => c.Parameters == "_id=dup");
        (east.Shard, east.Method).ShouldBe(("east", ShardSearchMethod.PostSearch));
        int calls = host.Shards.Calls.Count;

        SearchResult second = await host.FollowAsync(first.Link("next").ShouldNotBeNull());
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Entries.ShouldBe([(SearchWorld.WestDupUrl, Body("west"))]);
        second.Link("next").ShouldBeNull();
        ShardCall west = host.Shards.Calls.Skip(calls).ShouldHaveSingleItem();
        (west.Shard, west.Method, west.Parameters).ShouldBe(("west", ShardSearchMethod.Get, "_id=dup"));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "&_summary=count")]
    public async Task SourcePermissionPrecedesTheShapeGate(bool post, string controls)
    {
        using SearchHost host = Start(dialects: false, configure: RestrictSource);

        SearchResult refused = await SendAsync(host, post, Has + controls);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, "&_summary=count", true)]
    [InlineData(true, "&_count=0", true)]
    [InlineData(true, "&_total=accurate", true)]
    [InlineData(false, "", false)]
    public async Task UnsupportedOrUnconfiguredSearchDoesNotEvenCreateTheClient(bool post, string controls, bool dialects)
    {
        using SearchHost host = Start(dialects);

        SearchResult refused = await SendAsync(host, post, Has + controls);

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NotImplemented)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task ValidPassthroughPlanIsNotExecutedAndStillChecksSourcePermission(bool restricted, HttpStatusCode status)
    {
        using SearchHost host = Start(configure: s =>
        {
            s.Settings["Federation:ChainResolution:Passthrough:0"] = "Observation.subject:Patient";
            if (restricted)
            {
                RestrictSource(s);
            }
        });

        SearchResult result = await SendAsync(host, true, Has);

        result.StatusCode.ShouldBe(status);
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UncertifiedCountShapeRetainsThePlannerRefusal()
    {
        using SearchHost host = Start(configure: s =>
            s.Capabilities = SearchWorld.Capabilities(reverseChain: true));

        SearchResult result = await SendAsync(host, false, Has + "&_count=0");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("not-supported");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task IndependentClausesIntersectByOwnerAndPreserveRepeatedWirePairsOnReplay()
    {
        using SearchHost host = Start();
        host.Shards.FilterGeneratedIds = true;
        host.Shards.Resources = CorpusWithForeignWitness();
        const string query = Has + "&_count=1&_has:Observation:subject:_id=obs-dup&name=%4Frchid";

        SearchResult first = await SendAsync(host, true, query);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Entries.ShouldBe([(SearchWorld.EastDupUrl, Body("east"))]);
        const string wire = "_id=dup&_id=dup&name=%4Frchid";
        host.Shards.Calls.Single(c => c.Parameters == wire).Method.ShouldBe(ShardSearchMethod.Get);
        host.Shards.Calls.Count(c => c.Parameters == "code=oracle-code").ShouldBe(2);
        host.Shards.Calls.Count(c => c.Parameters == "_id=obs-dup").ShouldBe(2);

        SearchResult second = await host.FollowAsync(first.Link("next").ShouldNotBeNull());

        second.Entries.ShouldBe([(SearchWorld.WestDupUrl, Body("west"))]);
        second.Link("next").ShouldBeNull();
        host.Shards.Calls[^1].Parameters.ShouldBe(wire);
    }

    [Fact]
    public async Task NoWitnessKeysReturnsAnEmptyTerminalSearchsetWithoutAnyOwnerRead()
    {
        using SearchHost host = Start();
        host.Shards.FilterGeneratedIds = true;

        SearchResult result = await SendAsync(host, false, "_has:Observation:subject:_id=nonexistent");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Json.GetProperty("type").GetString().ShouldBe("searchset");
        result.Entries.ShouldBeEmpty();
        result.Link("next").ShouldBeNull();
        host.Shards.Calls.Select(c => c.Parameters).ShouldBe(["_id=nonexistent", "_id=nonexistent"]);
    }

    [Fact]
    public async Task OversizeGeneratedRequestIsCheckedWithTheOwnersVerbAndDoesNotReadTargets()
    {
        using SearchHost host = Start();
        host.Shards.RequestFits = request =>
        {
            request.Shard.Value.ShouldBe("east");
            request.Method.ShouldBe(ShardSearchMethod.Get);
            request.PageSize.ShouldBe(4);
            request.Parameters.ShouldBe([new ParameterOccurrence("_id", "dup"), new("%5Fid", "dup")]);
            return false;
        };

        SearchResult result = await SendAsync(host, true, Has + "&%5Fid=dup&_count=4");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("too-costly");
        host.Shards.Calls.Select(c => c.Parameters).ShouldBe(["code=oracle-code", "code=oracle-code"]);
    }

    [Fact]
    public async Task DiscoveryFailureRefusesThePageWithoutLeakingWitnessDetails()
    {
        using SearchHost host = Start(configure: s => s.Shards.FailingShard = "west");

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        result.Body.ShouldContain("p9xm");
        result.Body.ShouldNotContain("oracle-code");
        result.Body.ShouldNotContain("secret-path");
        result.Entries.ShouldBeEmpty();
        result.Link("next").ShouldBeNull();
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "code=oracle-code");
    }

    [Fact]
    public async Task RetriedWitnessCallsExhaustTheLiveBudgetBeforeAnyTargetCall()
    {
        using SearchHost host = Start(configure: s =>
        {
            s.Settings["Federation:Limits:BackendCallsPerShard"] = "2";
            s.Shards.SendsPerCall = 2;
        });

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("too-costly");
        host.Shards.Calls.Select(c => c.Parameters).ShouldBe(["code=oracle-code", "code=oracle-code"]);
    }

    [Fact]
    public async Task LiveDeadlineWinsOverDiscoveryTransportFailure()
    {
        var time = new ManualTimeProvider();
        using SearchHost host = Start(configure: s =>
        {
            s.Time = time;
            s.Shards.FailingShard = "east";
            s.Shards.DuringCall = () => time.Advance(TimeSpan.FromMinutes(1));
        });

        SearchResult result = await SendAsync(host, false, Paged);

        result.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        result.IssueCode.ShouldBe("timeout");
        result.Body.ShouldNotContain("k7q2");
        host.Shards.Calls.ShouldNotBeEmpty();
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "code=oracle-code");
    }

    [Fact]
    public async Task ContinuationPinsTheNonOwnerWitnessShardAndResumesOnlyTheOwnerCursor()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost host = Start(configure: s => s.Protection = ring.CreateProvider());
        host.Shards.FilterGeneratedIds = true;
        host.Shards.Resources = CorpusWithForeignWitness(includeWestRelative: false);
        SearchResult first = await SendAsync(host, false, Paged);
        first.Entries.ShouldBe([(SearchWorld.EastDupUrl, Body("east"))]);
        string next = first.Link("next").ShouldNotBeNull();
        (ContinuationProtector protector, _, RedeemedContinuation issued) = Redeem(ring, next);
        var consulted = new HashSet<(ContinuationDependencyGroup, string)>();
        SearchCapabilities view = Capabilities();
        protector.Redeem(new Uri(next).Query["?ct=".Length..], Binding(), (group, key) =>
        {
            consulted.Add((group, key));
            return Revision(view, group, key);
        }).Outcome.ShouldBe(ContinuationRedemptionOutcome.Redeemed);
        consulted.ShouldBe(new HashSet<(ContinuationDependencyGroup, string)>
        {
            (ContinuationDependencyGroup.Shard, "east"), (ContinuationDependencyGroup.Shard, "west"),
            (ContinuationDependencyGroup.Definition, "Observation.subject"), (ContinuationDependencyGroup.Definition, "Observation.code"),
            (ContinuationDependencyGroup.Definition, "Patient._id"),
        }, ignoreOrder: true);
        SearchContinuationFrame.TryRead(issued.Snapshot.AsSpan(), out SearchContinuationFrame? frame)
            .ShouldBe(UnsortedSnapshotOutcome.Success);
        var codec = new UnsortedSnapshotCodec(new TokenOptions().MaxInlineTokenBytes, view.Registry);
        codec.Decode(frame!.Snapshot, out UnsortedContinuationSnapshot? saved).ShouldBe(UnsortedSnapshotOutcome.Success);
        saved!.Streams.ShouldHaveSingleItem().Shard.Value.ShouldBe("east");
        int calls = host.Shards.Calls.Count;

        SearchResult second = await host.FollowAsync(next);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.Entries.ShouldBe([("https://fhir-east.example.org/Patient/east-only",
            Encoding.UTF8.GetString(SearchWorld.Slice("east", "Patient", "east-only")))]);
        second.Link("next").ShouldBeNull();
        ShardCall followed = host.Shards.Calls.Skip(calls).ShouldHaveSingleItem();
        (followed.Shard, followed.Target is not null).ShouldBe(("east", true));
    }

    [Fact]
    public async Task RedemptionUsesCurrentSourcePermissionForTheSameBoundCaller()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s => s.Protection = ring.CreateProvider());
        using SearchHost restricted = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            RestrictSource(s);
        });
        issuer.Shards.FilterGeneratedIds = true;
        SearchResult first = await SendAsync(issuer, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        SearchResult refused = await restricted.FollowAsync(first.Link("next").ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        restricted.Shards.Views.ShouldBeEmpty();
        restricted.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RedemptionWithoutAnExplicitDialectMapRefusesBeforeClientCreation()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s => s.Protection = ring.CreateProvider());
        using SearchHost unconfigured = Start(dialects: false, configure: s => s.Protection = ring.CreateProvider());
        issuer.Shards.FilterGeneratedIds = true;
        SearchResult first = await SendAsync(issuer, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        SearchResult refused = await unconfigured.FollowAsync(first.Link("next").ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        unconfigured.Shards.Views.ShouldBeEmpty();
        unconfigured.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistryGrowthRequiresRestartEvenWhenTheReverseOwnerSubsetHasNotChanged(bool reverse)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            s.Shards.FilterGeneratedIds = true;
            s.Shards.Resources = CorpusWithForeignWitness(includeWestRelative: false);
        });
        using SearchHost grown = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = Capabilities(SearchWorld.ReverseRegistry(extraWitnessShard: true));
            foreach (var (key, value) in SearchWorld.Settings(extraWitnessShard: true))
            {
                s.Settings[key] = value;
            }
        });
        SearchResult first = await SendAsync(issuer, false, reverse ? Paged : "_count=1");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        if (reverse)
        {
            ReadBookmark(ring, next).Saved.Streams.ShouldHaveSingleItem().Shard.Value.ShouldBe("east");
        }

        SearchResult refused = await grown.FollowAsync(next);

        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        refused.IssueCode.ShouldBe("not-found");
        grown.Shards.Views.ShouldBeEmpty();
        grown.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RedemptionRebuildsTheSavedWirePredicatesBeforeCreatingTheClient()
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost host = Start(configure: s => s.Protection = ring.CreateProvider());
        host.Shards.FilterGeneratedIds = true;
        SearchResult first = await SendAsync(host, true, Paged + "&name=%4Frchid");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, next);
        ShardSearchRequest[] streams = [.. saved.Streams];
        ShardSearchRequest west = streams[1];
        streams[1] = new ShardSearchRequest(west.Shard, west.Type,
            [new("_id", "dup"), new("name", "Orchid")], west.PageSize, west.Method);
        var changed = new UnsortedContinuationSnapshot(streams, saved.PageSize, UnsortedContinuation.AtStart(west.Shard));
        codec.Encode(changed, out byte[]? encoded).ShouldBe(UnsortedSnapshotOutcome.Success);
        ContinuationIssue forged = protector.IssueSuccessor(issued,
            new SearchContinuationFrame(frame.Post, frame.RawQuery, encoded.ShouldNotBeNull()).ToArray());
        int calls = host.Shards.Calls.Count;

        SearchResult refused = await host.GetAsync("/Patient?ct=" + forged.InlineToken.ShouldNotBeNull());

        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        host.Shards.Views.Count.ShouldBe(1);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Theory]
    [InlineData("owner order")]
    [InlineData("missing occurrence")]
    [InlineData("noncanonical generated encoding")]
    [InlineData("empty ID")]
    [InlineData("invalid ID")]
    [InlineData("duplicate IDs")]
    [InlineData("clauses disagree")]
    [InlineData("wrong method")]
    [InlineData("wrong stream page size")]
    [InlineData("wrong snapshot page size")]
    [InlineData("current key bound")]
    public async Task AuthenticatedBookmarkMustReconstructEveryOwnerRequest(string change)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s => s.Protection = ring.CreateProvider());
        issuer.Shards.FilterGeneratedIds = true;
        const string query = Has + "&_count=1&_has:Observation:subject:_id=obs-dup&%5Fid=dup";
        SearchResult first = await SendAsync(issuer, true, query);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, next);
        ShardSearchRequest[] streams = [.. saved.Streams];
        streams.Length.ShouldBe(2);
        ShardSearchRequest east = streams[0];
        east.Shard.Value.ShouldBe("east");
        east.Parameters.ShouldBe([new ParameterOccurrence("_id", "dup"), new("_id", "dup"), new("%5Fid", "dup")]);
        east.Method.ShouldBe(ShardSearchMethod.Get);
        int pageSize = saved.PageSize;
        switch (change)
        {
            case "owner order":
                Array.Reverse(streams);
                break;
            case "missing occurrence":
                streams[0] = Copy(east, parameters: [east.Parameters[0], east.Parameters[2]]);
                break;
            case "noncanonical generated encoding":
                streams[0] = WithIds("%64up", "dup");
                break;
            case "empty ID":
                streams[0] = WithIds("dup%2C", "dup%2C");
                break;
            case "invalid ID":
                streams[0] = WithIds("dup%2Fbad", "dup%2Fbad");
                break;
            case "duplicate IDs":
                streams[0] = WithIds("dup%2Cdup", "dup%2Cdup");
                break;
            case "clauses disagree":
                streams[0] = WithIds("dup", "east-only");
                break;
            case "wrong method":
                streams[0] = Copy(east, method: ShardSearchMethod.PostSearch);
                break;
            case "wrong stream page size":
                streams[0] = Copy(east, pageSize: 2);
                break;
            case "wrong snapshot page size":
                pageSize = 2;
                streams[0] = Copy(east, pageSize: 2);
                streams[1] = Copy(streams[1], pageSize: 2);
                break;
            case "current key bound":
                break;
            default:
                throw new InvalidOperationException("Unknown test case.");
        }

        using SearchHost redeemer = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            if (change == "current key bound")
            {
                s.Settings["Federation:Limits:MaxChainKeys"] = "1";
            }
        });
        var changed = new UnsortedContinuationSnapshot(streams, pageSize, UnsortedContinuation.AtStart(east.Shard));
        string token = change == "current key bound"
            ? new Uri(next).Query["?ct=".Length..]
            : IssueBookmark(protector, issued, frame, codec, changed);

        await AssertRestartWithoutClientAsync(redeemer, token);

        ShardSearchRequest WithIds(string firstIds, string secondIds) =>
            Copy(east, parameters: [new("_id", firstIds), new("_id", secondIds), east.Parameters[2]]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BookmarkMustFitTheOwnersCurrentReplayProfile(bool changeCursorPolicy)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            s.Shards.FilterGeneratedIds = true;
            s.Shards.Resources = CorpusWithForeignWitness(includeWestRelative: false);
        });
        SearchResult first = await SendAsync(issuer, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        string next = first.Link("next").ShouldNotBeNull();
        var (protector, issued, frame, codec, saved) = ReadBookmark(ring, next);
        ShardSearchRequest east = saved.Streams.ShouldHaveSingleItem();
        east.Method.ShouldBe(ShardSearchMethod.PostSearch);
        saved.Continuation.Cursor!.Redemption.ShouldBe(NextLinkRedemption.FollowGet);
        var profile = new BackendProfile(SearchWorld.PostCapable.Name, SupportsPostSearch: true,
            changeCursorPolicy ? 100 : 1,
            changeCursorPolicy ? NextLinkRedemption.RepostToSearch : NextLinkRedemption.FollowGet, null);
        using SearchHost redeemer = Start(configure: s =>
        {
            s.Protection = ring.CreateProvider();
            s.Capabilities = Capabilities(SearchWorld.ReverseRegistry(extraWitnessShard: false, profile));
        });

        string token;
        if (changeCursorPolicy)
        {
            token = new Uri(next).Query["?ct=".Length..];
        }
        else
        {
            // Same outer page size and saved template; only the earlier exact send is too big for this owner now.
            ShardSearchRequest template = Copy(east, pageSize: 2);
            var anchor = new ResourceKey(east.Shard, east.Type, ResourceId.Parse("dup"));
            var changed = new UnsortedContinuationSnapshot([template], 2, UnsortedContinuation.AtSearch(template, anchor));
            token = IssueBookmark(protector, issued, frame, codec, changed, rawQuery: Has + "&_count=2");
        }

        await AssertRestartWithoutClientAsync(redeemer, token);
    }

    [Theory]
    [InlineData("missing definition")]
    [InlineData("missing owner")]
    [InlineData("wrong caller")]
    public async Task InvalidManifestAndWrongCallerAreNotClassifiedAsRegistryGrowth(string change)
    {
        using var ring = new IsolatedKeyRing();
        using SearchHost issuer = Start(configure: s => s.Protection = ring.CreateProvider());
        using SearchHost redeemer = Start(configure: s => s.Protection = ring.CreateProvider());
        issuer.Shards.FilterGeneratedIds = true;
        SearchResult first = await SendAsync(issuer, false, Paged);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (protector, issued, _, _, _) = ReadBookmark(ring, first.Link("next").ShouldNotBeNull());
        SearchCapabilities view = Capabilities();
        var shards = view.Registry.Shards.Where(s => change != "missing owner" || s.Id.Value != "west")
            .Select(s => KeyValuePair.Create(s.Id.Value, view.Registry.ComputeVersion([s.Id])));
        List<KeyValuePair<string, string>> definitions =
        [
            KeyValuePair.Create("Observation.subject", Revision(view, ContinuationDependencyGroup.Definition, "Observation.subject").ShouldNotBeNull()),
            KeyValuePair.Create("Observation.code", Revision(view, ContinuationDependencyGroup.Definition, "Observation.code").ShouldNotBeNull()),
        ];
        if (change != "missing definition")
        {
            definitions.Add(KeyValuePair.Create("Patient._id", Revision(view, ContinuationDependencyGroup.Definition, "Patient._id").ShouldNotBeNull()));
        }

        ContinuationBinding binding = change == "wrong caller"
            ? ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "different-subject",
                "different-oid", null, [SearchWorld.ReaderRole])
            : issued.Binding;
        ContinuationIssue invalid = protector.Issue(binding, ContinuationDependencySet.Create(shards, definitions), issued.Snapshot.AsSpan());

        SearchResult refused = await redeemer.GetAsync("/Patient?ct=" + invalid.InlineToken.ShouldNotBeNull());

        refused.StatusCode.ShouldBe(change == "wrong caller" ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest);
        refused.IssueCode.ShouldBe(change == "wrong caller" ? "forbidden" : "invalid");
        redeemer.Shards.Views.ShouldBeEmpty();
        redeemer.Shards.Calls.ShouldBeEmpty();
    }

    private static async Task AssertRestartWithoutClientAsync(SearchHost host, string token)
    {
        SearchResult refused = await host.GetAsync("/Patient?ct=" + token);
        refused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        refused.IssueCode.ShouldBe("not-found");
        host.Shards.Views.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    private static ShardSearchRequest Copy(ShardSearchRequest request, int? pageSize = null,
        ShardSearchMethod? method = null, ParameterOccurrence[]? parameters = null) =>
        new(request.Shard, request.Type, parameters ?? request.Parameters, pageSize ?? request.PageSize, method ?? request.Method);

    private static (ContinuationProtector Protector, RedeemedContinuation Issued, SearchContinuationFrame Frame,
        UnsortedSnapshotCodec Codec, UnsortedContinuationSnapshot Saved) ReadBookmark(IsolatedKeyRing ring, string next)
    {
        var (protector, view, issued) = Redeem(ring, next);
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

    private static Dictionary<string, IReadOnlyList<SearchWorld.SeedResource>> CorpusWithForeignWitness(bool includeWestRelative = true)
    {
        var resources = SearchWorld.Resources.ToDictionary(StringComparer.Ordinal);
        const string witness = """
            {"resourceType":"Observation","id":"east-witness","code":{"coding":[{"code":"oracle-code"}]},
            "subject":{"reference":"https://fhir-east.example.org/Patient/east-only"}}
            """;
        resources["west"] = [.. resources["west"].Where(r => includeWestRelative || r.Id != "obs-dup"),
            new("Observation", "east-witness", Encoding.UTF8.GetBytes(witness))];
        return resources;
    }

    private static ContinuationBinding Binding() =>
        ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject",
            SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]);

    private static (ContinuationProtector Protector, SearchCapabilities View, RedeemedContinuation Issued) Redeem(IsolatedKeyRing ring, string next)
    {
        SearchCapabilities view = Capabilities();
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        RedeemedContinuation issued = protector.Redeem(new Uri(next).Query["?ct=".Length..], Binding(),
            (group, key) => Revision(view, group, key)).Continuation.ShouldNotBeNull();
        return (protector, view, issued);
    }

    private static string? Revision(SearchCapabilities view, ContinuationDependencyGroup group, string key) =>
        group == ContinuationDependencyGroup.Shard
            ? view.Registry.ComputeVersion([ShardId.Parse(key)])
            : key.Split('.', 2) is [string type, string code]
                ? view.GetResolvedDefinitionRevision(ResourceType.Parse(type), code)
                : null;

    private static void RestrictSource(SearchHostSetup setup) => setup.Settings.Remove($"Federation:Host:Roles:{SearchWorld.ReaderRole}:1");

    private static Task<SearchResult> SendAsync(SearchHost host, bool post, string query) =>
        post ? host.PostFormAsync("/Patient/_search", query) : host.GetAsync("/Patient?" + query);

    private static string Body(string shard) => Encoding.UTF8.GetString(SearchWorld.Slice(shard, "Patient", "dup"));

    private static SearchHost Start(bool dialects = true, Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            setup.Capabilities = Capabilities();
            configure?.Invoke(setup);
            if (dialects)
            {
                setup.ConfigureServices = services => services.AddSingleton<IReadOnlyDictionary<ShardId, CountDialect>>(
                    setup.Capabilities!.Registry.Shards.ToDictionary(s => s.Id, _ => CountDialect.CountZero));
            }
        });

    private static SearchCapabilities Capabilities(ShardRegistry? registry = null) =>
        SearchWorld.Capabilities(exactCountShapes: ["Patient?", "Patient?_id=,"], reverseChain: true, registry: registry);
}

using System.Net;
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
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// Redeeming <c>ct</c>: the caller is re-authorised and re-bound on every page, and the stored query is replanned on the
/// current view and must admit exactly the same shard requests before any shard is called.
/// </summary>
public sealed class PrimarySearchContinuationTests
{
    private const string PagedQuery = "/Patient?name=Orchid&_count=1";

    [Fact]
    public async Task Next_ReChecksTheCallersRoleGrants()
    {
        using var host = SearchHost.Start();
        string next = await FirstNextAsync(host);
        int calls = host.Shards.Calls.Count;

        SearchResult result = await host.FollowAsync(next, host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole], ObjectId = SearchWorld.CallerObjectId }));

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Theory]
    [InlineData("another-subject", SearchWorld.CallerObjectId, null, new[] { SearchWorld.ReaderRole })]
    [InlineData("synthetic-subject", "another-oid", null, new[] { SearchWorld.ReaderRole })]
    [InlineData("synthetic-subject", null, SearchWorld.CallerObjectId, new[] { SearchWorld.ReaderRole })]
    [InlineData("synthetic-subject", SearchWorld.CallerObjectId, null, new[] { SearchWorld.ReaderRole, SearchWorld.ObservationRole })]
    public async Task Next_FromAnotherCallerBinding_IsForbidden_WithoutAnyShardCall(string subject, string? oid, string? client, string[] roles)
    {
        using var host = SearchHost.Start();
        string next = await FirstNextAsync(host);
        int calls = host.Shards.Calls.Count;

        SearchResult result = await host.FollowAsync(
            next,
            host.Token(new SyntheticTokenRequest { Subject = subject, ObjectId = oid, ClientId = client, Roles = roles }));

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Theory]
    [InlineData("/Patient?ct=abc&name=Orchid")]
    [InlineData("/Patient?name=Orchid&ct=abc")]
    [InlineData("/Patient?ct=abc&ct=abc")]
    [InlineData("/Patient?ct=")]
    [InlineData("/Patient?ct=not*base64url")]
    [InlineData("/Patient?ct=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedOrMixedContinuation_IsInvalid_WithoutAnyShardCall(string pathAndQuery)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync(pathAndQuery);

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("invalid");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValidContinuationMixedWithOtherParameters_IsInvalid()
    {
        using var host = SearchHost.Start();
        string next = await FirstNextAsync(host);
        int calls = host.Shards.Calls.Count;

        SearchResult result = await host.GetAsync(SearchHost.ToLocal(next) + "&name=Orchid");

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task ContinuationOnPost_IsInvalid_InTheUrlOrTheBody()
    {
        using var host = SearchHost.Start();
        string next = await FirstNextAsync(host);
        string ct = next[(next.IndexOf("?ct=", StringComparison.Ordinal) + 4)..];
        int calls = host.Shards.Calls.Count;

        SearchResult inBody = await host.PostFormAsync("/Patient/_search", $"ct={ct}");
        SearchResult inUrl = await host.PostFormAsync($"/Patient/_search?ct={ct}", string.Empty);

        inBody.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inUrl.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task ContinuationForAnotherType_IsInvalid_WithoutAnyShardCall()
    {
        using var host = SearchHost.Start();
        string next = await FirstNextAsync(host);
        int calls = host.Shards.Calls.Count;

        SearchResult result = await host.GetAsync("/Observation" + SearchHost.ToLocal(next)["/Patient".Length..]);

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task ContinuationFromAnotherKeyRing_IsInvalid()
    {
        using var issuer = SearchHost.Start();
        using var other = SearchHost.Start();
        string next = await FirstNextAsync(issuer);

        SearchResult result = await other.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        other.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExpiredContinuation_IsGone_WithoutAnyShardCall()
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = clock;
            setup.Settings["Federation:Tokens:SlidingLifetime"] = "00:01:00";
        });
        string next = await FirstNextAsync(host);
        int calls = host.Shards.Calls.Count;

        clock.Now += TimeSpan.FromMinutes(2);
        SearchResult result = await host.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.Gone);
        host.Shards.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task AnotherInstanceSharingTheKeyRingAndView_RedeemsTheContinuation()
    {
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var redeemer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        string next = await FirstNextAsync(issuer);

        SearchResult result = await redeemer.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Single().FullUrl.ShouldBe("https://fhir-east.example.org/Patient/east-only");
    }

    [Fact]
    public async Task AdmittedPredicateNoLongerSupported_IsGone_BeforeAnyShardCall()
    {
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var drifted = SearchHost.Start(setup =>
        {
            setup.Protection = ring.CreateProvider();
            setup.Capabilities = SearchWorld.Capabilities(patientName: false);
        });
        string next = await FirstNextAsync(issuer);

        SearchResult result = await drifted.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.Gone);
        drifted.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DifferentRevisionOfAnAdmittedDefinition_IsGone_BeforeAnyShardCall()
    {
        using var ring = new IsolatedKeyRing();
        SearchCapabilities view = SearchWorld.Capabilities();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var redeemer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        string next = await FirstNextAsync(issuer);
        string token = new Uri(next).Query["?ct=".Length..];
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        var caller = ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject",
            SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]);
        var revisions = view.Registry.Shards.ToDictionary(s => s.Id.Value, s => view.Registry.ComputeVersion([s.Id]), StringComparer.Ordinal);
        string revision = view.GetResolvedDefinitionRevision(ResourceType.Parse("Patient"), "name").ShouldNotBeNull();
        RedeemedContinuation issued = protector.Redeem(token, caller, (group, key) =>
            group == ContinuationDependencyGroup.Shard ? revisions.GetValueOrDefault(key) : key == "Patient.name" ? revision : null)
            .Continuation.ShouldNotBeNull();

        var stale = ContinuationDependencySet.Create(revisions, [KeyValuePair.Create("Patient.name", "prior-expression-revision")]);
        ContinuationIssue previous = protector.Issue(caller, stale, issued.Snapshot.AsSpan());
        SearchResult result = await redeemer.GetAsync("/Patient?ct=" + previous.InlineToken.ShouldNotBeNull());

        result.StatusCode.ShouldBe(HttpStatusCode.Gone);
        redeemer.Shards.Calls.ShouldBeEmpty();
        (await redeemer.FollowAsync(next)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("extra")]
    public async Task AuthenticatedButInconsistentDependencyManifest_IsInvalid_WithoutAnyShardCall(string shape)
    {
        // Every carried revision is current, so this is not drift: the same replay simply does not depend on that set.
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var redeemer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        string next = await FirstNextAsync(issuer);
        (ContinuationProtector protector, RedeemedContinuation issued) = RedeemOutsideTheRoute(ring, next);
        SearchCapabilities view = SearchWorld.Capabilities();
        KeyValuePair<string, string>[] definitions = shape == "extra"
            ? [CurrentDefinition(view, "Patient", "name"), CurrentDefinition(view, "Patient", "family")]
            : [];

        var manifest = ContinuationDependencySet.Create(ShardRevisions(view), definitions);
        ContinuationIssue forged = protector.Issue(issued.Binding, manifest, issued.Snapshot.AsSpan());
        SearchResult result = await redeemer.GetAsync("/Patient?ct=" + forged.InlineToken.ShouldNotBeNull());

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("invalid");
        redeemer.Shards.Calls.ShouldBeEmpty();
        (await redeemer.FollowAsync(next)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Gone)]
    [InlineData(2, HttpStatusCode.OK)]
    public async Task SuccessorReachingTheChainDeadline_IsGone_WithoutABundle(int millisecondsBeforeDeadline, HttpStatusCode expected)
    {
        // The page is served while the chain is live, but its successor would be issued at (or past) the chain deadline,
        // where it can never be redeemed. The control is one millisecond earlier and remains followable.
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var clock = new SettableClock(start);
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = clock;
            setup.Settings["Federation:Tokens:SlidingLifetime"] = "00:01:00";
            setup.Settings["Federation:Tokens:AbsoluteLifetime"] = "00:01:00";
        });
        string next = await FirstNextAsync(host);
        DateTimeOffset deadline = start + TimeSpan.FromMinutes(1);
        DateTimeOffset served = deadline - TimeSpan.FromMilliseconds(millisecondsBeforeDeadline - 1);

        clock.Now = deadline - TimeSpan.FromMilliseconds(millisecondsBeforeDeadline);
        host.Shards.DuringCall = () => clock.Now = served;
        SearchResult result = await host.FollowAsync(next);

        result.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.Gone)
        {
            result.Body.ShouldNotContain("Bundle");
            return;
        }

        result.Entries.Single().FullUrl.ShouldBe("https://fhir-east.example.org/Patient/east-only");
        (await host.FollowAsync(result.Link("next").ShouldNotBeNull())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(false, 999, HttpStatusCode.OK)]
    [InlineData(false, 1000, HttpStatusCode.Gone)]
    [InlineData(true, 999, HttpStatusCode.OK)]
    [InlineData(true, 1000, HttpStatusCode.Gone)]
    public async Task StagedPageDoesNotPublishAnExpiredInlineLink(bool successor, int protectionMilliseconds, HttpStatusCode expected)
    {
        var clock = new SettableClock(DateTimeOffset.UtcNow);
        using var ring = new IsolatedKeyRing();
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = clock;
            setup.Protection = new AfterProtectProvider(ring.CreateProvider(),
                () => clock.Now += TimeSpan.FromMilliseconds(protectionMilliseconds));
            setup.Settings["Federation:Tokens:SlidingLifetime"] = "00:00:01";
        });
        using var issuer = SearchHost.Start(setup =>
        {
            setup.Time = clock;
            setup.Protection = ring.CreateProvider();
        });

        SearchResult result = successor
            ? await host.FollowAsync(await FirstNextAsync(issuer))
            : await host.GetAsync(PagedQuery);

        result.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.OK)
        {
            (await issuer.FollowAsync(result.Link("next").ShouldNotBeNull())).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        else
        {
            result.Body.ShouldNotContain("Bundle");
        }
    }

    [Theory]
    [InlineData(true, HttpStatusCode.GatewayTimeout)]
    [InlineData(false, HttpStatusCode.Gone)]
    public async Task MissingReplayAnchor_IsGone_UnlessTheRequestDeadlinePassedFirst(bool deadlinePasses, HttpStatusCode expected)
    {
        // The anchor is found missing only once the replayed page is back; a deadline that passed by then is the outcome.
        var time = new ManualTimeProvider();
        using var ring = new IsolatedKeyRing();
        using var host = SearchHost.Start(setup =>
        {
            setup.Time = time;
            setup.Protection = ring.CreateProvider();
        });
        string next = await FirstNextAsync(host);
        (ContinuationProtector protector, RedeemedContinuation issued) = RedeemOutsideTheRoute(ring, next);
        SearchContinuationFrame.TryRead(issued.Snapshot.AsSpan(), out SearchContinuationFrame? read).ShouldBe(UnsortedSnapshotOutcome.Success);
        SearchContinuationFrame frame = read.ShouldNotBeNull();
        var codec = new UnsortedSnapshotCodec(new TokenOptions().MaxInlineTokenBytes, SearchWorld.Capabilities().Registry);
        codec.Decode(frame.Snapshot, out UnsortedContinuationSnapshot? decoded).ShouldBe(UnsortedSnapshotOutcome.Success);
        UnsortedContinuationSnapshot snapshot = decoded.ShouldNotBeNull();
        ShardSearchRequest stream = snapshot.Streams[0];
        var absent = new UnsortedContinuationSnapshot(snapshot.Streams, snapshot.PageSize,
            UnsortedContinuation.AtSearch(stream, new ResourceKey(stream.Shard, stream.Type, ResourceId.Parse("absent"))));
        codec.Encode(absent, out byte[]? encoded).ShouldBe(UnsortedSnapshotOutcome.Success);
        ContinuationIssue successor = protector.IssueSuccessor(
            issued, new SearchContinuationFrame(frame.Post, frame.RawQuery, encoded.ShouldNotBeNull()).ToArray());
        int calls = host.Shards.Calls.Count;
        if (deadlinePasses)
        {
            host.Shards.Answered = () => time.Advance(TimeSpan.FromMinutes(1));
        }

        SearchResult result = await host.GetAsync("/Patient?ct=" + successor.InlineToken.ShouldNotBeNull());

        result.StatusCode.ShouldBe(expected);
        result.Body.ShouldNotContain("Bundle");
        host.Shards.Calls.Count.ShouldBeGreaterThan(calls);
    }

    [Fact]
    public async Task UnrelatedDefinitionChange_StillReplaysTheContinuation()
    {
        // Only the definitions the query's predicates resolved to are pinned: a view that also searches
        // `organization` changes nothing the `name` continuation depends on.
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var changed = SearchHost.Start(setup =>
        {
            setup.Protection = ring.CreateProvider();
            setup.Capabilities = SearchWorld.Capabilities(organizationMissing: true);
        });
        string next = await FirstNextAsync(issuer);

        SearchResult result = await changed.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Single().FullUrl.ShouldBe("https://fhir-east.example.org/Patient/east-only");
        changed.Shards.Calls.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("/Patient?_count=1")]
    [InlineData(PagedQuery)]
    public async Task ContinuationForATypeTheCurrentViewNoLongerSearches_IsGone_BeforeAnyShardCall(string query)
    {
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var drifted = SearchHost.Start(setup =>
        {
            setup.Protection = ring.CreateProvider();
            setup.Capabilities = SearchWorld.Capabilities(patient: false);
        });
        string next = (await issuer.GetAsync(query)).Link("next")!;

        SearchResult result = await drifted.FollowAsync(next);

        result.StatusCode.ShouldBe(HttpStatusCode.Gone);
        result.IssueCode.ShouldNotBe("invalid");
        drifted.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangedPageSizeLimit_IsGone_BeforeAnyShardCall()
    {
        using var ring = new IsolatedKeyRing();
        using var issuer = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        using var drifted = SearchHost.Start(setup =>
        {
            setup.Protection = ring.CreateProvider();
            setup.Settings["Federation:Limits:DefaultPageSize"] = "1";
            setup.Settings["Federation:Limits:MaxPageSize"] = "1";
        });
        SearchResult first = await issuer.GetAsync("/Patient?name=Orchid&_count=2");

        SearchResult result = await drifted.FollowAsync(first.Link("next")!);

        result.StatusCode.ShouldBe(HttpStatusCode.Gone);
        drifted.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimNamesDifferingOnlyInCase_AreNotTheBindingClaims()
    {
        // Claim names are exact: an "OID" or "Roles" claim is neither the caller's oid nor a role, so it neither makes the
        // oid ambiguous nor changes the binding of a later page.
        using var host = SearchHost.Start();
        SearchResult first = await host.GetAsync(PagedQuery, host.Token(DefaultCaller(new() { ["OID"] = ["someone-else"] })));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        SearchResult result = await host.FollowAsync(
            first.Link("next").ShouldNotBeNull(),
            host.Token(DefaultCaller(new() { ["Roles"] = ["Federation.Admin"] })));

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Single().FullUrl.ShouldBe("https://fhir-east.example.org/Patient/east-only");
    }

    [Fact]
    public async Task ClientIdClaim_BindsACallerWithoutAnOid()
    {
        using var host = SearchHost.Start();
        SearchResult first = await host.GetAsync(PagedQuery, host.Token(AppCaller(new() { ["client_id"] = ["app-1"] })));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body);
        string next = first.Link("next").ShouldNotBeNull();
        int calls = host.Shards.Calls.Count;

        SearchResult other = await host.FollowAsync(next, host.Token(AppCaller(new() { ["client_id"] = ["app-2"] })));
        other.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.Count.ShouldBe(calls);

        SearchResult same = await host.FollowAsync(next, host.Token(AppCaller(new() { ["client_id"] = ["app-1"] })));
        same.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("azp", "client_id", true)]
    [InlineData("azp", "appid", true)]
    [InlineData("appid", "client_id", true)]
    [InlineData("azp", "client_id", false)]
    public async Task ConflictingClientIdForms_AreForbidden_WithoutAnyShardCall(string first, string second, bool withOid)
    {
        using var host = SearchHost.Start();
        var claims = new Dictionary<string, string[]> { [first] = ["app-1"], [second] = ["app-2"] };

        SearchResult result = await host.GetAsync(PagedQuery, host.Token(withOid ? DefaultCaller(claims) : AppCaller(claims)));

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AgreeingClientIdForms_BindTheSameCaller()
    {
        using var host = SearchHost.Start();
        SearchResult first = await host.GetAsync(PagedQuery, host.Token(AppCaller(new() { ["azp"] = ["app-1"], ["client_id"] = ["app-1"] })));
        first.StatusCode.ShouldBe(HttpStatusCode.OK, first.Body);

        SearchResult result = await host.FollowAsync(first.Link("next").ShouldNotBeNull(), host.Token(AppCaller(new() { ["appid"] = ["app-1"] })));

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("RepeatedClientId")]
    [InlineData("RepeatedOid")]
    public async Task RepeatedSingleValuedClaims_AreForbidden_WithoutAnyShardCall(string shape)
    {
        using var host = SearchHost.Start();
        SyntheticTokenRequest request = shape == "RepeatedClientId"
            ? AppCaller(new() { ["client_id"] = ["app-1", "app-1"] })
            : AppCaller(new() { ["oid"] = ["oid-1", "oid-2"] });

        SearchResult result = await host.GetAsync(PagedQuery, host.Token(request));

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("02")] // another frame version
    [InlineData("01")] // truncated frame
    [InlineData("0105000000000200000000")] // unknown origin
    [InlineData("0100000000000164")] // another snapshot codec version
    [InlineData("010000000000")] // no snapshot at all
    public async Task AuthenticatedButMalformedOrUnsupportedSnapshot_IsInvalid_WithoutAnyShardCall(string snapshotHex)
    {
        // Only expiry, drift and a missing anchor are 410; well-protected bytes this host cannot read are a bad request.
        using var ring = new IsolatedKeyRing();
        using var host = SearchHost.Start(setup => setup.Protection = ring.CreateProvider());
        var registry = SearchWorld.Capabilities().Registry;
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        ContinuationIssue issue = protector.Issue(
            ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject", SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]),
            ContinuationDependencySet.Create(registry.Shards.Select(s => KeyValuePair.Create(s.Id.Value, registry.ComputeVersion([s.Id]))), []),
            Convert.FromHexString(snapshotHex));

        SearchResult result = await host.GetAsync("/Patient?ct=" + issue.InlineToken.ShouldNotBeNull());

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("invalid");
        host.Shards.Calls.ShouldBeEmpty();
    }

    private static SyntheticTokenRequest DefaultCaller(Dictionary<string, string[]> extra) =>
        new() { Roles = [SearchWorld.ReaderRole], ObjectId = SearchWorld.CallerObjectId, ExtraClaims = extra };

    private static SyntheticTokenRequest AppCaller(Dictionary<string, string[]> extra) =>
        new() { Roles = [SearchWorld.ReaderRole], ObjectId = null, ExtraClaims = extra };

    /// <summary>Redeems a genuine token as its issuing caller, outside any host, against the default view's revisions.</summary>
    private static (ContinuationProtector Protector, RedeemedContinuation Issued) RedeemOutsideTheRoute(IsolatedKeyRing ring, string next)
    {
        SearchCapabilities view = SearchWorld.Capabilities();
        Dictionary<string, string> shards = ShardRevisions(view).ToDictionary(StringComparer.Ordinal);
        var protector = new ContinuationProtector(ring.CreateProvider(), new TokenOptions(), TimeProvider.System);
        var caller = ContinuationBinding.Create(SyntheticIdentityProvider.TrustedIssuer, "synthetic-subject",
            SearchWorld.CallerObjectId, null, [SearchWorld.ReaderRole]);
        RedeemedContinuation issued = protector.Redeem(new Uri(next).Query["?ct=".Length..], caller, (group, key) =>
            group == ContinuationDependencyGroup.Shard
                ? shards.GetValueOrDefault(key)
                : key.Split('.', 2) is [string type, string code] ? view.GetResolvedDefinitionRevision(ResourceType.Parse(type), code) : null)
            .Continuation.ShouldNotBeNull();
        return (protector, issued);
    }

    private static IEnumerable<KeyValuePair<string, string>> ShardRevisions(SearchCapabilities view) =>
        view.Registry.Shards.Select(s => KeyValuePair.Create(s.Id.Value, view.Registry.ComputeVersion([s.Id])));

    private static KeyValuePair<string, string> CurrentDefinition(SearchCapabilities view, string type, string code) =>
        KeyValuePair.Create($"{type}.{code}", view.GetResolvedDefinitionRevision(ResourceType.Parse(type), code).ShouldNotBeNull());

    private static async Task<string> FirstNextAsync(SearchHost host)
    {
        SearchResult first = await host.GetAsync(PagedQuery);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        return first.Link("next").ShouldNotBeNull();
    }

    private sealed class AfterProtectProvider(IDataProtectionProvider inner, Action afterProtect) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose) => new AfterProtect(inner.CreateProtector(purpose), afterProtect);

        private sealed class AfterProtect(IDataProtector inner, Action afterProtect) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new AfterProtect(inner.CreateProtector(purpose), afterProtect);

            public byte[] Protect(byte[] plaintext)
            {
                byte[] ciphertext = inner.Protect(plaintext);
                afterProtect();
                return ciphertext;
            }

            public byte[] Unprotect(byte[] protectedData) => inner.Unprotect(protectedData);
        }
    }
}

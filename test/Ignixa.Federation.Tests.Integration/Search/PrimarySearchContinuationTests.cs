using System.Net;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Continuations;
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

    private static async Task<string> FirstNextAsync(SearchHost host)
    {
        SearchResult first = await host.GetAsync(PagedQuery);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        return first.Link("next").ShouldNotBeNull();
    }
}

using System.Net;
using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Authentication;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>The public primary search through the real <c>Program</c>: first pages, links, paging and backend requests.</summary>
public sealed class PrimarySearchTests
{
    private const string EastOnlyUrl = "https://fhir-east.example.org/Patient/east-only";
    private const string WestOnlyUrl = "https://fhir-west.example.org/Patient/west-only";
    private const string NextPattern = @"\Ahttps://fhir\.example\.test/federation/Patient\?ct=[A-Za-z0-9_-]+\z";

    [Fact]
    public async Task Get_ReturnsEveryShardsResourcesAsExactSlices_WithAbsoluteLinksFromTheConfiguredBase()
    {
        using var host = SearchHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Patient?name=Orchid");
        request.Headers.Host = "attacker.example";
        request.Headers.Add("X-Forwarded-Host", "forwarded.example");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Add("X-Forwarded-Prefix", "/elsewhere");

        SearchResult result = await host.SendAsync(request, host.Token());

        result.StatusCode.ShouldBe(HttpStatusCode.OK, result.Body);
        result.MediaType.ShouldBe("application/fhir+json");
        result.Charset.ShouldBe("utf-8");
        result.Json.GetProperty("resourceType").GetString().ShouldBe("Bundle");
        result.Json.GetProperty("type").GetString().ShouldBe("searchset");
        result.Entries.Select(e => e.FullUrl).ShouldBe([SearchWorld.EastDupUrl, EastOnlyUrl, SearchWorld.WestDupUrl, WestOnlyUrl]);
        result.Entries[0].Resource.ShouldBe(Encoding.UTF8.GetString(SearchWorld.Slice("east", "Patient", "dup")));
        result.Entries[1].Resource.ShouldBe(Encoding.UTF8.GetString(SearchWorld.Slice("east", "Patient", "east-only")));
        result.Entries[2].Resource.ShouldBe(Encoding.UTF8.GetString(SearchWorld.Slice("west", "Patient", "dup")));
        result.Entries[3].Resource.ShouldBe(Encoding.UTF8.GetString(SearchWorld.Slice("west", "Patient", "west-only")));
        result.Entries[0].Resource.ShouldContain($"\"{SearchWorld.ExpectedGiven[SearchWorld.EastDupUrl]}\"");
        result.Entries[2].Resource.ShouldContain($"\"{SearchWorld.ExpectedGiven[SearchWorld.WestDupUrl]}\"");
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name=Orchid");
        result.Link("next").ShouldBeNull();
        result.Body.ShouldNotContain("attacker.example");
        result.Body.ShouldNotContain("forwarded.example");
        result.Body.ShouldNotContain("elsewhere");
        result.Body.ShouldNotContain("alternate");
        result.Body.ShouldNotContain("_getpages");

        host.Shards.Calls.Select(c => c.Shard).Distinct().Order().ShouldBe(["east", "west"]);
        host.Shards.Calls.ShouldAllBe(c => c.Method == ShardSearchMethod.Get && c.Parameters == "name=Orchid");
    }

    [Fact]
    public async Task Get_ForwardsEveryPredicateOccurrenceVerbatimAndKeepsTheOriginalSpellingInSelf()
    {
        using var host = SearchHost.Start();
        const string Query = "name=Orchid&_count=5&name=Or%20chid&family=Ce+dar&_id=dup,east-only";

        SearchResult result = await host.GetAsync($"/Patient?{Query}");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?{Query}");
        host.Shards.Calls.ShouldNotBeEmpty();
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "name=Orchid&name=Or%20chid&family=Ce+dar&_id=dup,east-only");
        host.Shards.Calls.Single(c => c.Shard == "east").PageSize.ShouldBe(5);
    }

    [Fact]
    public async Task Get_WithoutParameters_SearchesTheWholeType()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Patient");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Count.ShouldBe(4);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient");
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == string.Empty);
    }

    [Fact]
    public async Task Post_MergesTheUrlQueryThenTheBody_AndPostsOnlyToShardsThatSupportIt()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.PostFormAsync("/Patient/_search?name=Orchid", "family=Orchid&name=Or%20chid");

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Count.ShouldBe(4);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name=Orchid&family=Orchid&name=Or%20chid");
        host.Shards.Calls.ShouldAllBe(c => c.Parameters == "name=Orchid&family=Orchid&name=Or%20chid");
        host.Shards.Calls.Single(c => c.Shard == "east").Method.ShouldBe(ShardSearchMethod.PostSearch);
        host.Shards.Calls.Single(c => c.Shard == "west").Method.ShouldBe(ShardSearchMethod.Get);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded; charset=UTF-8")]
    [InlineData("Application/X-WWW-Form-UrlEncoded")]
    public async Task Post_AcceptsFormUrlEncodedUtf8(string mediaType)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.PostFormAsync("/Patient/_search", "name=Orchid", mediaType: mediaType);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name=Orchid");
    }

    [Fact]
    public async Task Paging_WalksEveryPageThroughOpaqueNextLinks_InRegistryOrder()
    {
        using var host = SearchHost.Start();
        var seen = new List<string>();

        SearchResult page = await host.GetAsync("/Patient?name=Orchid&_count=1");
        int pages = 1;
        while (true)
        {
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            page.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name=Orchid&_count=1");
            page.Body.ShouldNotContain("_getpages");
            page.Body.ShouldNotContain("opaque-");
            page.Entries.Count.ShouldBe(1);
            seen.Add(page.Entries[0].FullUrl);
            if (page.Link("next") is not { } next)
            {
                break;
            }

            next.ShouldMatch(NextPattern);
            page = await host.FollowAsync(next);
            pages++;
        }

        pages.ShouldBe(4);
        seen.ShouldBe([SearchWorld.EastDupUrl, EastOnlyUrl, SearchWorld.WestDupUrl, WestOnlyUrl]);
        host.Shards.Calls.Where(c => c.Method is not null).ShouldAllBe(c => c.Parameters == "name=Orchid" && c.Method == ShardSearchMethod.Get);
    }

    [Fact]
    public async Task Paging_AfterPost_KeepsEachShardsOriginalMethod()
    {
        using var host = SearchHost.Start();

        SearchResult page = await host.PostFormAsync("/Patient/_search", "name=Orchid&_count=1");
        int pages = 1;
        while (page.Link("next") is { } next)
        {
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            page.Link("self").ShouldBe($"{SearchWorld.PublicBase}/Patient?name=Orchid&_count=1");
            page = await host.FollowAsync(next);
            pages++;
        }

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        pages.ShouldBe(4);
        host.Shards.Calls.Where(c => c.Shard == "east" && c.Method is not null).ShouldAllBe(c => c.Method == ShardSearchMethod.PostSearch);
        host.Shards.Calls.Where(c => c.Shard == "west" && c.Method is not null).ShouldAllBe(c => c.Method == ShardSearchMethod.Get);
        host.Shards.Calls.ShouldContain(c => c.Shard == "west" && c.Method == ShardSearchMethod.Get);
    }

    [Fact]
    public async Task Continuation_RedeemedTwice_ReturnsTheSamePage()
    {
        using var host = SearchHost.Start();
        SearchResult first = await host.GetAsync("/Patient?name=Orchid&_count=1");
        string next = first.Link("next")!;

        SearchResult once = await host.FollowAsync(next);
        SearchResult twice = await host.FollowAsync(next);

        once.StatusCode.ShouldBe(HttpStatusCode.OK);
        twice.StatusCode.ShouldBe(HttpStatusCode.OK);
        once.Entries.ShouldBe(twice.Entries);
        once.Entries.Single().FullUrl.ShouldBe(EastOnlyUrl);
    }

    [Fact]
    public async Task Search_GivesTheTransportOnlyTheRegisteredView()
    {
        SearchHostSetup? captured = null;
        using var host = SearchHost.Start(setup => captured = setup);

        (await host.GetAsync("/Patient?name=Orchid")).StatusCode.ShouldBe(HttpStatusCode.OK);

        host.Shards.Views.ShouldHaveSingleItem().ShouldBeSameAs(captured!.Capabilities);
    }

    [Fact]
    public async Task Observation_IsSearchableByARoleGrantedOnlyObservation()
    {
        using var host = SearchHost.Start();
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole], ObjectId = SearchWorld.CallerObjectId });

        SearchResult result = await host.GetAsync("/Observation?code=oracle-code", token);

        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        result.Entries.Select(e => e.FullUrl).ShouldBe([
            "https://fhir-east.example.org/Observation/obs-dup",
            "https://fhir-west.example.org/Observation/obs-dup",
            "https://fhir-west.example.org/Observation/obs-dup-xref",
        ]);
    }
}

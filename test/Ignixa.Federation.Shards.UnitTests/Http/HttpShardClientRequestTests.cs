using System.Text;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>What the client puts on the wire, observed at a real TLS loopback server.</summary>
public sealed class HttpShardClientRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ShardSearchMethod, string, string, NextLinkRedemption, string> StartCombinations => new()
    {
        { ShardSearchMethod.Get, "SyntheticRepost", "GET /fhir/Patient?name=Sarah&_count=10", NextLinkRedemption.FollowGet, "GET" },
        { ShardSearchMethod.Get, "SyntheticFollow", "GET /fhir/Patient?name=Sarah&_count=10", NextLinkRedemption.FollowGet, "GET" },
        { ShardSearchMethod.PostSearch, "SyntheticRepost", "POST /fhir/Patient/_search", NextLinkRedemption.RepostToSearch, "POST" },
        { ShardSearchMethod.PostSearch, "SyntheticFollow", "POST /fhir/Patient/_search", NextLinkRedemption.FollowGet, "GET" },
    };

    [Theory]
    [MemberData(nameof(StartCombinations))]
    public async Task StartMethodAndProfile_DecideTheFirstExchangeAndTheRedemption(
        ShardSearchMethod start,
        string profile,
        string firstExchange,
        NextLinkRedemption redemption,
        string continuationMethod)
    {
        await using var shard = ShardHarness.Start(profile == "SyntheticRepost" ? ShardHarness.Repost : ShardHarness.Follow);
        string next = shard.Search + "?name=Sarah&_count=10&ct=c2Vjb25k";
        shard.Server.Respond(
            ScriptedResponse.Json(shard.SarahPage(next)),
            ScriptedResponse.Json(Searchsets.Page(self: null)));

        ShardPage first = await shard.Client.SearchAsync(shard.SarahRequest(start), Ct);
        await shard.Client.ContinueAsync(first.Next!, Ct);

        RecordedRequest[] requests = [.. shard.Server.Requests];
        $"{requests[0].Method} {requests[0].Target}".ShouldBe(firstExchange);
        first.Next!.Redemption.ShouldBe(redemption);
        first.Next.Target.ShouldBe(next);
        requests[1].Method.ShouldBe(continuationMethod);
    }

    [Fact]
    public async Task Get_SendsEveryOccurrenceVerbatimInOrderThenOneCount()
    {
        await using var shard = ShardHarness.Start();
        const string query = "name=%41%2fb&code=http://loinc.org|1234&given=J+Q&x=a=b&e=&name=Sarah&name=Sarah&q=%7e~&_count=10";
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?" + query)));
        var request = shard.Request(
            ShardSearchMethod.Get,
            10,
            "name", "%41%2fb", "code", "http://loinc.org|1234", "given", "J+Q", "x", "a=b", "e", string.Empty,
            "name", "Sarah", "name", "Sarah", "q", "%7e~");

        await shard.Client.SearchAsync(request, Ct);

        RecordedRequest sent = shard.Server.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe("GET");
        sent.Target.ShouldBe("/fhir/Patient?" + query);
        sent.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task PostSearch_SendsTheSameQueryAsAFormBodyToTheTypedSearchEndpoint()
    {
        await using var shard = ShardHarness.Start();
        const string query = "name=%41%2fb&given=J+Q&e=&name=Sarah&name=Sarah&_count=25";
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?" + query)));
        var request = shard.Request(ShardSearchMethod.PostSearch, 25, "name", "%41%2fb", "given", "J+Q", "e", string.Empty, "name", "Sarah", "name", "Sarah");

        await shard.Client.SearchAsync(request, Ct);

        RecordedRequest sent = shard.Server.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe("POST");
        sent.Target.ShouldBe("/fhir/Patient/_search");
        Encoding.ASCII.GetString(sent.Body).ShouldBe(query);
        sent.HeaderValues("Content-Type").ShouldBe(["application/x-www-form-urlencoded"]);
    }

    [Fact]
    public async Task CountOnly_SendsCountZero()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?name=Sarah&_count=0", total: 42)));

        ShardPage page = await shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 0, "name", "Sarah"), Ct);

        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient?name=Sarah&_count=0");
        page.Total.ShouldBe(42);
        page.Entries.ShouldBeEmpty();
        page.IsExhausted.ShouldBeTrue();
    }

    [Fact]
    public async Task EveryExchange_SendsStrictHandlingFhirJsonAndTheBearerTokenOnly()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ScriptedResponse.Json(shard.SarahPage(next: shard.Search + "?ct=Mg")),
            ScriptedResponse.Json(Searchsets.Page(self: null, next: shard.Search + "?ct=Mw")),
            ScriptedResponse.Json(Searchsets.Page(self: null)));

        ShardPage first = await shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
        ShardPage second = await shard.Client.ContinueAsync(first.Next!, Ct);
        await shard.Client.ContinueAsync(second.Next!, Ct);

        shard.Server.Requests.Count.ShouldBe(3);
        foreach (RecordedRequest sent in shard.Server.Requests)
        {
            sent.HeaderValues("Prefer").ShouldBe(["handling=strict"]);
            sent.HeaderValues("Accept").ShouldBe(["application/fhir+json"]);
            sent.HeaderValues("Authorization").ShouldBe(["Bearer " + SyntheticCredentials.Token]);
            sent.HeaderValues("Cookie").ShouldBeEmpty();
            sent.HeaderValues("Accept-Encoding").ShouldBeEmpty();
        }

        shard.Credentials.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task PostSearch_OnAProfileWithoutPost_IsRejectedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start(ShardHarness.GetOnly);

        await Should.ThrowAsync<ArgumentException>(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PageSizeAboveTheProfileMaxCount_IsRejectedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 51, "name", "Sarah"), Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task PageSizeAtTheProfileMaxCount_IsSent()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?_count=50")));

        await shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 50), Ct);

        shard.Server.Requests.ShouldHaveSingleItem().Target.ShouldBe("/fhir/Patient?_count=50");
    }

    [Theory]
    [InlineData("name", "Sarah Smith")]
    [InlineData("name", "Sarah\tSmith")]
    [InlineData("name", "Zo\u00eb")]
    [InlineData("na me", "Sarah")]
    [InlineData("name", "100%")]
    [InlineData("name", "%4")]
    [InlineData("name", "%zz")]
    [InlineData("name", "a\r\nX-Injected: 1")]
    public async Task OccurrenceTextThatCannotBeSentVerbatim_IsRejectedBeforeAnyCall(string key, string value)
    {
        await using var shard = ShardHarness.Start();

        await Should.ThrowAsync<ArgumentException>(() => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 10, key, value), Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequestForAnotherShard_IsRejectedBeforeAnyCall()
    {
        await using var shard = ShardHarness.Start();
        var west = new ShardSearchRequest(ShardId.Parse("west"), ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 10, ShardSearchMethod.Get);

        await Should.ThrowAsync<ArgumentException>(() => shard.Client.SearchAsync(west, Ct));

        shard.Credentials.Calls.ShouldBe(0);
        shard.Server.Requests.ShouldBeEmpty();
    }
}

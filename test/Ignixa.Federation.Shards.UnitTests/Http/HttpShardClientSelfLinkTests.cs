using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// The first page's <c>self</c> link must report exactly the decoded pairs sent, as a multiset (design spec §6.2): a
/// backend that silently dropped, repeated or added a predicate answered a different search.
/// </summary>
public sealed class HttpShardClientSelfLinkTests
{
    // Sent: name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&_count=10
    private static readonly string[] Parameters = ["name", "%41b", "code", "a|b", "given", "J+Q", "name", "Sarah", "name", "Sarah"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&_count=10")]
    [InlineData("_count=10&name=Sarah&given=J%20Q&name=Sarah&code=a%7Cb&name=Ab")]
    [InlineData("name=Sarah&name=Ab&name=Sarah&code=a%7cb&given=J+Q&_count=%31%30")]
    [InlineData("name=Sarah&&name=Ab&name=Sarah&code=a|b&given=J+Q&_count=10&")]
    public async Task SelfReportingTheSentPairs_InAnyOrderAndEquivalentSpelling_IsAccepted(string selfQuery)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?" + selfQuery)));

        ShardPage page = await shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 10, Parameters), Ct);

        page.IsExhausted.ShouldBeTrue();
    }

    [Theory]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&name=Sarah&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&_count=10&active=true")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&_count=10&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=Sarah&name=Sarah&_count=20")]
    [InlineData("name=%41b&code=a&given=J+Q&name=Sarah&name=Sarah&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name=sarah&name=Sarah&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J+Q&name:exact=Sarah&name=Sarah&_count=10")]
    [InlineData("name=%41b&code=a|b&given=J%2BQ&name=Sarah&name=Sarah&_count=10")]
    [InlineData("")]
    public async Task SelfThatDropsRepeatsAddsOrChangesAPair_IsAMismatch(string selfQuery)
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?" + selfQuery, total: 1, entries: Searchsets.Entry(Searchsets.Patient("p1")))));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.Request(ShardSearchMethod.Get, 10, Parameters), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.SelfLinkMismatch);
        failure.Message.ShouldNotContain("Sarah");
    }

    [Fact]
    public async Task SelfWithoutAQuery_IsAMismatch()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.SelfLinkMismatch);
    }

    [Fact]
    public async Task SelfForAPostSearch_IsComparedWithTheFormBody()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Searchsets.Page(shard.Search + "?name=Sarah")));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
            () => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.SelfLinkMismatch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("two")]
    [InlineData("name=Sarah&_count=10&bad=%zz")]
    [InlineData("name=Sarah&_count=10&bad=a b")]
    public async Task MissingDuplicatedOrUndecodableSelf_IsMalformed(string? defect)
    {
        await using var shard = ShardHarness.Start();
        string body = defect switch
        {
            null => Searchsets.Page(self: null),
            "two" => Searchsets.Page(shard.Search + "?name=Sarah&_count=10").Replace(
                "\"link\":[",
                $"\"link\":[{{\"relation\":\"self\",\"url\":\"{shard.Search}?name=Sarah&_count=10\"}},",
                StringComparison.Ordinal),
            _ => Searchsets.Page(shard.Search + "?" + defect),
        };
        shard.Server.Respond(ScriptedResponse.Json(body));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task ContinuationPages_AreNotComparedWithTheirSelf()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(
            ScriptedResponse.Json(shard.SarahPage(next: shard.Search + "?name=Sarah&_count=10&ct=Mg")),
            ScriptedResponse.Json(Searchsets.Page(shard.Search + "?ct=Mg&unrelated=1", next: shard.Search + "?ct=Mw")),
            ScriptedResponse.Json(Searchsets.Page(self: null)));

        ShardPage first = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);
        ShardPage second = await shard.Client.ContinueAsync(first.Next!, Ct);
        ShardPage third = await shard.Client.ContinueAsync(second.Next!, Ct);

        third.IsExhausted.ShouldBeTrue();
    }
}

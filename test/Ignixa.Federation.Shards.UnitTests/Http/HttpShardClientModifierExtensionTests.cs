using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A <c>modifierExtension</c> may change what its element means, so a searchset is refused when one sits on a Bundle
/// element the client reads its framing from: a link, an entry, or the entry's search. An
/// ordinary <c>extension</c>, an empty <c>modifierExtension</c>, and anything inside the returned resource, which is
/// passed through as received, are not framing and stay accepted.
/// </summary>
public sealed class HttpShardClientModifierExtensionTests
{
    private const string Modifier = ",\"modifierExtension\":[{\"url\":\"http://example.test/modifier\",\"valueBoolean\":true}]";

    private const string HarmlessFraming = ",\"extension\":[{\"url\":\"http://example.test/ordinary\",\"valueBoolean\":true}],\"modifierExtension\":[]";

    private const string Resource = "{\"resourceType\":\"Patient\",\"id\":\"p1\",\"extension\":[{\"url\":\"http://example.test/ordinary\",\"valueBoolean\":true}],\"modifierExtension\":[{\"url\":\"http://example.test/modifier\",\"valueBoolean\":true}],\"name\":[{\"extension\":[{\"url\":\"http://example.test/ordinary\",\"valueString\":\"x\"}]}]}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Bundle elements the client inspects to establish the page's framing.</summary>
    public static TheoryData<string> FramingElements =>
    [
        "selfLink",
        "nextLink",
        "ignoredLink",
        "entry",
        "search",
    ];

    private static string Body(ShardHarness shard, string placement, string member)
    {
        string At(string name) => name == placement ? member : string.Empty;

        return $$"""
            {"resourceType":"Bundle","type":"searchset","link":[
              {"relation":"self","url":"{{shard.Search}}?name=Sarah&_count=10"{{At("selfLink")}}},
              {"relation":"first","url":"?ct=Zmlyc3Q"{{At("ignoredLink")}}},
              {"relation":"next","url":"?ct=Mg"{{At("nextLink")}}}],
             "entry":[{"fullUrl":"Patient/p1","resource":{{Resource}},"search":{"mode":"match"{{At("search")}}}{{At("entry")}}}]}
            """;
    }

    [Theory]
    [MemberData(nameof(FramingElements))]
    public async Task ModifierExtension_OnAFramingElement_IsMalformedAndNothingFollows(string placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Body(shard, placement, Modifier)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
        failure.Message.ShouldNotContain("Sarah");
        shard.Server.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(",\"modifierExtension\":{\"url\":\"http://example.test/modifier\"}")]
    [InlineData(",\"modifierExtension\":null")]
    public async Task ModifierExtension_ThatIsNotAnEmptyArray_IsRefusedWhateverItsShape(string member)
    {
        ArgumentNullException.ThrowIfNull(member);
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Body(shard, "entry", member)));

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        failure.Failure.ShouldBe(ShardCallFailure.MalformedResponse);
    }

    [Fact]
    public async Task HarmlessFramingExtensions_DoNotAffectThePageOrItsOpaqueResource()
    {
        await using var shard = ShardHarness.Start();
        shard.Server.Respond(ScriptedResponse.Json(Body(shard, "entry", HarmlessFraming)));

        ShardPage page = await shard.Client.SearchAsync(shard.SarahRequest(), Ct);

        ShardEntry entry = page.Entries.ShouldHaveSingleItem();
        entry.Key.Id.Value.ShouldBe("p1");
        entry.Resource.ToArray().ShouldBe(Encoding.UTF8.GetBytes(Resource));
        page.Next.ShouldNotBeNull().Target.ShouldBe("?ct=Mg");
    }
}

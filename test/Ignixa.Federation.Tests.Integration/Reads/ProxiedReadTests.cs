using System.Text.Json;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Tests.Integration.Search;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>The proxied read serves each owner's exact bytes, and search, metadata and the route agree on whether it is enabled.</summary>
public sealed class ProxiedReadTests
{
    private const string EastAlternate = SearchWorld.PublicBase + "/shards/k7q2/Patient/dup";
    private const string WestAlternate = SearchWorld.PublicBase + "/shards/p9xm/Patient/dup";

    internal static SearchHost Start(ScriptedReadBinding? binding, Action<SearchHostSetup>? configure = null) =>
        SearchHost.Start(setup =>
        {
            if (binding is not null)
            {
                setup.ConfigureServices = services => services.AddSingleton<IShardReadBinding>(binding);
            }

            configure?.Invoke(setup);
        });

    [Fact]
    public async Task SearchDerivedAlternates_ReadTheSameIdFromEachOwner_WithMatchingContentLocationAndExactBytes()
    {
        var binding = new ScriptedReadBinding();
        using var host = Start(binding);

        SearchResult search = await host.GetAsync("/Patient?name=Orchid");

        JsonElement[] entries = [.. search.Json.GetProperty("entry").EnumerateArray()];
        var alternates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement entry in entries)
        {
            alternates[entry.GetProperty("fullUrl").GetString()!] = entry.GetProperty("link").EnumerateArray()
                .Single(l => l.GetProperty("relation").GetString() == "alternate").GetProperty("url").GetString()!;
        }

        alternates[SearchWorld.EastDupUrl].ShouldBe(EastAlternate);
        alternates[SearchWorld.WestDupUrl].ShouldBe(WestAlternate);
        byte[] eastBytes = SearchWorld.Slice("east", "Patient", "dup");
        byte[] westBytes = SearchWorld.Slice("west", "Patient", "dup");
        eastBytes.ShouldNotBe(westBytes);

        foreach ((string fullUrl, string shard, byte[] expected) in new[]
        {
            (SearchWorld.EastDupUrl, "east", eastBytes),
            (SearchWorld.WestDupUrl, "west", westBytes),
        })
        {
            ReadResponse read = await ReadResponse.GetAsync(host, SearchHost.ToLocal(alternates[fullUrl]), host.Token());

            read.Status.ShouldBe(200);
            read.ContentLocation.ShouldBe(fullUrl);
            read.ContentType.ShouldBe("application/fhir+json; charset=utf-8");
            read.Bytes.ShouldBe(expected);
            binding.Calls[^1].Key.Shard.Value.ShouldBe(shard);
            binding.Calls[^1].Key.Type.Value.ShouldBe("Patient");
            binding.Calls[^1].Key.Id.Value.ShouldBe("dup");
        }

        binding.Calls.Count.ShouldBe(2);
        binding.Calls.ShouldAllBe(c => c.Registry.Version == SearchWorld.Registry.Version);
        binding.Calls.ShouldAllBe(c => c.TokenIsBudgetToken);
    }

    [Fact]
    public async Task OwnerSelection_UsesTheAliasNotTheShardId()
    {
        var binding = new ScriptedReadBinding();
        using var host = Start(binding);

        ReadResponse byId = await ReadResponse.GetAsync(host, "/shards/east/Patient/dup", host.Token());
        ReadResponse unknown = await ReadResponse.GetAsync(host, "/shards/zzzz/Patient/dup", host.Token());
        ReadResponse notAnAlias = await ReadResponse.GetAsync(host, "/shards/K7Q2/Patient/dup", host.Token());

        foreach (ReadResponse refused in new[] { byId, unknown, notAnAlias })
        {
            refused.Status.ShouldBe(404);
            refused.IssueCode.ShouldBe("not-found");
            refused.IsCleanFailure.ShouldBeTrue();
        }

        byId.Body.ShouldBe(unknown.Body);
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EnabledHost_PublishesReadAndAlternates()
    {
        using var host = Start(new ScriptedReadBinding());

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);
        SearchResult search = await host.GetAsync("/Patient?name=Orchid");

        string[][] interactions = [.. statement.GetProperty("rest")[0].GetProperty("resource").EnumerateArray()
            .Select(r => r.GetProperty("interaction").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToArray())];
        interactions.ShouldNotBeEmpty();
        interactions.ShouldAllBe(i => i.SequenceEqual(new[] { "read", "search-type" }));
        search.Body.ShouldContain("\"alternate\"");
    }

    [Fact]
    public async Task UnboundHost_PublishesNeitherReadNorAlternates_AndTheRouteIsNotFound_WithoutBindingIo()
    {
        using var host = Start(binding: null);

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);
        SearchResult search = await host.GetAsync("/Patient?name=Orchid");
        ReadResponse read = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/dup", host.Token());

        statement.GetRawText().ShouldNotContain("\"read\"");
        search.Body.ShouldNotContain("alternate");
        read.Status.ShouldBe(404);
        read.IsCleanFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("false", true)]
    [InlineData("true", false)]
    [InlineData("false", false)]
    public async Task OneEnablementSnapshot_ConfigurationAndBinding_AgreeAcrossSearchMetadataAndRoute(string proxyReads, bool bound)
    {
        var binding = bound ? new ScriptedReadBinding() : null;
        using var host = Start(binding, setup => setup.Settings["Federation:ProxyReads"] = proxyReads);

        JsonElement statement = JsonElement.Parse((await ReadResponse.GetAsync(host, "/metadata", token: null)).Body);
        SearchResult search = await host.GetAsync("/Patient?name=Orchid");
        ReadResponse read = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/dup", host.Token());

        statement.GetRawText().ShouldNotContain("\"read\"");
        search.Body.ShouldNotContain("alternate");
        read.Status.ShouldBe(404);
        binding?.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfiguredAndBound_IsEnabled_EvenWhenProxyReadsIsExplicit()
    {
        var binding = new ScriptedReadBinding();
        using var host = Start(binding, setup => setup.Settings["Federation:ProxyReads"] = "true");

        ReadResponse read = await ReadResponse.GetAsync(host, "/shards/p9xm/Patient/west-only", host.Token());

        read.Status.ShouldBe(200);
        read.Bytes.ShouldBe(SearchWorld.Slice("west", "Patient", "west-only"));
        read.ContentLocation.ShouldBe("https://fhir-west.example.org/Patient/west-only");
    }

    [Fact]
    public async Task UnconfiguredHost_AnswersTheProxiedRouteUnavailable()
    {
        var binding = new ScriptedReadBinding();
        using var host = Start(binding, setup => setup.Unconfigure());

        ReadResponse read = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/dup", host.Token());

        read.Status.ShouldBe(503);
        read.IsCleanFailure.ShouldBeTrue();
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchRoutes_AreUnchangedByTheReadRoutes()
    {
        using var host = Start(new ScriptedReadBinding());

        ReadResponse getSearch = await ReadResponse.GetAsync(host, "/Patient/_search", host.Token());
        ReadResponse history = await ReadResponse.GetAsync(host, "/Patient/_history", host.Token());
        ReadResponse versioned = await ReadResponse.GetAsync(host, "/Patient/dup/_history/1", host.Token());

        // GET [type]/_search was 405 before the unqualified read route existed; it is now one of that route's non-matches.
        getSearch.Status.ShouldBe(404);
        history.Status.ShouldBe(404);
        versioned.Status.ShouldBe(404);
    }
}

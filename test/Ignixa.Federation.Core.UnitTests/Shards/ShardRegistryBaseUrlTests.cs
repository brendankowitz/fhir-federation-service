using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardRegistryBaseUrlTests
{
    public static TheoryData<string, string> CanonicalForms => new()
    {
        { "https://East.Example:443/fhir/", "https://east.example/fhir" },
        { "HTTPS://east.example/Fhir/R4", "https://east.example/Fhir/R4" },
        { "https://east.example", "https://east.example" },
        { "https://east.example/", "https://east.example" },
        { "https://east.example:8443/fhir", "https://east.example:8443/fhir" },
        { "https://[::1]:443/fhir", "https://[::1]/fhir" },
        { "https://b\u00fccher.example/fhir", "https://xn--bcher-kva.example/fhir" },
        { "https://east.example/a%20b", "https://east.example/a%20b" },
        { "https://east.example/a%2fb", "https://east.example/a%2Fb" },
        { "https://east.example/a%abb", "https://east.example/a%ABb" },
        { "https://east.example/a%2f", "https://east.example/a%2F" },
    };

    // TheoryData<T>.Add disallows null; the null case is real configuration (a missing BaseUrl).
    public static TheoryData<string?> Rejected => new()
    {
        null!,
        "",
        "   ",
        "east.example/fhir",
        "/fhir",
        "http://east.example/fhir",
        "ftp://east.example/fhir",
        "https://user:pass@east.example/fhir",
        "https://east.example/fhir?x=1",
        "https://east.example/fhir?",
        "https://east.example/fhir#top",
        "https://east.example/fhir#",
        "https://east.example/a/../fhir",
        "https://east.example/a/./fhir",
        "https://east.example/a/%2e%2E/fhir",
        "https://east.example/fhir/..",
        "https://east.example/fhir/.. ",
        "https://east.example//fhir",
        "https://east.example/a//fhir",
        "https://east.example/fhir//",
        "https://east.example/a\\..\\fhir",
        "https://east.example./fhir",
        "https://east.example.:8443/fhir",
    };

    [Theory]
    [MemberData(nameof(CanonicalForms))]
    public void BaseUrl_IsCanonicalised(string configured, string canonical)
    {
        ShardRegistry registry = Create(Shard("east", "k7q2", configured));

        registry.FullUrl(Key("east")).ShouldBe(canonical + "/Patient/A1");
        registry.Shards[0].CanonicalBaseUrl.ShouldBe(canonical);
        registry.Shards[0].BaseUrl.ShouldBe(new Uri(canonical));
    }

    [Theory]
    [MemberData(nameof(CanonicalForms))]
    public void LinkBaseAlias_IsCanonicalised(string configured, string canonical)
    {
        ShardRegistry registry = Create(Shard("east", "k7q2", "https://east-public.example/fhir", configured));

        registry.Shards[0].CanonicalLinkBaseAliases.ShouldBe([canonical]);
        registry.Shards[0].LinkBaseAliases.ShouldBe([new Uri(canonical)]);
    }

    [Fact]
    public void CanonicalText_OfARootBaseAndAnEscapedPath_IsExact()
    {
        ShardRegistry registry = Create(
            Shard("east", "k7q2", "https://east.example/"),
            Shard("west", "p9xm", "https://west.example/r%204/", "https://west-private.example/r%2f4"));

        registry.Shards[0].CanonicalBaseUrl.ShouldBe("https://east.example");
        registry.Shards[1].CanonicalBaseUrl.ShouldBe("https://west.example/r%204");
        registry.Shards[1].CanonicalLinkBaseAliases.ShouldBe(["https://west-private.example/r%2F4"]);
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void BaseUrl_OutsideTheRules_IsAnError(string? configured)
    {
        SingleError("Federation:Shards:0:BaseUrl", Shard("east", "k7q2", configured!));
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void LinkBaseAlias_OutsideTheRules_IsAnError(string? linkBaseAlias)
    {
        SingleError("Federation:Shards:0:LinkBaseAliases:1", Shard("east", "k7q2", "https://east.example/fhir", "https://east-private.example/fhir", linkBaseAlias!));
    }

    [Theory]
    [InlineData("https://east.example/fhir", "https://EAST.example:443/fhir/")]
    [InlineData("https://east.example", "https://east.example/")]
    [InlineData("https://east.example/a%2fb", "https://east.example/a%2Fb")]
    public void SameBaseOnTwoShards_IsAnError(string first, string second)
    {
        SingleError("Federation:Shards:1:BaseUrl", Shard("east", "k7q2", first), Shard("west", "p9xm", second));
    }

    [Fact]
    public void BaseEqualToAnotherShardsLinkAlias_IsAnError()
    {
        SingleError("Federation:Shards:1:BaseUrl", East("https://shared.example/fhir"), Shard("west", "p9xm", "https://shared.example/fhir"));
    }

    [Fact]
    public void LinkAliasEqualToAnotherShardsLinkAlias_IsAnError()
    {
        SingleError("Federation:Shards:1:LinkBaseAliases:0", East("https://shared.example/fhir"), West("https://shared.example/fhir"));
    }

    [Fact]
    public void LinkAliasEqualToItsOwnBase_IsAnError()
    {
        SingleError("Federation:Shards:0:LinkBaseAliases:0", East("https://east.example/fhir/"));
    }

    [Fact]
    public void RepeatedLinkAliasOnOneShard_IsAnError()
    {
        SingleError("Federation:Shards:0:LinkBaseAliases:1", East("https://east-private.example/fhir", "https://EAST-private.example/fhir"));
    }

    [Theory]
    [InlineData("https://east.example/fhir", "https://east.example/fhir/west")]
    [InlineData("https://east.example/fhir/west", "https://east.example/fhir")]
    [InlineData("https://east.example", "https://east.example/fhir")]
    public void NestedBasesOnDifferentShards_AreAnError(string first, string second)
    {
        SingleError("Federation:Shards:1:BaseUrl", Shard("east", "k7q2", first), Shard("west", "p9xm", second));
    }

    [Fact]
    public void LinkAliasNestedUnderAnotherShardsBase_IsAnError()
    {
        SingleError("Federation:Shards:1:LinkBaseAliases:0", East(), West("https://east.example/fhir/west"));
    }

    [Theory]
    [InlineData("https://east.example/r4", "https://east.example/r40")]
    [InlineData("https://east.example/r40", "https://east.example/r4")]
    [InlineData("https://east.example/fhir", "https://east.example:8443/fhir/west")]
    [InlineData("https://east.example/fhir", "https://east.example.org/fhir")]
    [InlineData("https://east.example/fhir", "https://east.example/FHIR")]
    public void BasesThatOnlyShareAPrefix_AreLegal(string first, string second)
    {
        Create(Shard("east", "k7q2", first), Shard("west", "p9xm", second)).Shards.Count.ShouldBe(2);
    }

    [Fact]
    public void NestedFormsOfTheSameShard_AreLegal()
    {
        Create(East("https://east.example/fhir/legacy")).Shards[0].LinkBaseAliases.Count.ShouldBe(1);
    }

    private static ResourceKey Key(string shard) => new(ShardId.Parse(shard), ResourceType.Parse("Patient"), ResourceId.Parse("A1"));
}

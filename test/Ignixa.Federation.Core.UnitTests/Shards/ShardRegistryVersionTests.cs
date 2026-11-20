using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Shards;

/// <summary>
/// The registry version is a compatibility contract (continuation tokens embed it), so its encoding is pinned by golden
/// values computed independently of this code.
/// </summary>
public sealed class ShardRegistryVersionTests
{
    [Fact]
    public void Version_OfOneShard_MatchesTheGoldenValue()
    {
        Create(East()).Version.ShouldBe("a82f6f3f31998f387952afadc7400fe4a474439f77125c63db77dbda8df138fc");
    }

    [Fact]
    public void Version_OfTwoShardsWithUnsortedLinkAliases_MatchesTheGoldenValue()
    {
        ShardRegistry registry = Create(
            East("https://east-b.example/fhir", "https://east-a.example/fhir"),
            Shard("west", "p9xm", "https://west.example").WithProfile("Ignixa"));

        registry.Version.ShouldBe("966972d0680ff1afa7126da4f6eb7bef292c9a0de4512995ccf1fec57e434b97");
    }

    [Fact]
    public void SubsetVersion_KeepsOriginalOrdinals_AndMatchesTheGoldenValue()
    {
        ShardRegistry registry = Create(East(), Shard("west", "p9xm", "https://west.example").WithProfile("Ignixa"));

        registry.ComputeVersion([ShardId.Parse("west")]).ShouldBe("98a292e93c1f73054e744a7e1dba9b6e77c640de11a976810e06c34935a3f1de");
    }

    [Theory]
    [InlineData("Id")]
    [InlineData("Alias")]
    [InlineData("BaseUrl")]
    [InlineData("BaseUrl path case")]
    [InlineData("LinkBaseAliases added")]
    [InlineData("Profile")]
    public void Version_ChangesWithEveryHashedField(string change)
    {
        Create(Changed(change)).Version.ShouldNotBe(Create(East()).Version);
    }

    [Theory]
    [InlineData("Auth type")]
    [InlineData("Auth client id")]
    [InlineData("trailing slash")]
    [InlineData("explicit :443")]
    [InlineData("host and scheme case")]
    public void Version_IgnoresAuthAndEquivalentUrlSpellings(string change)
    {
        Create(Changed(change)).Version.ShouldBe(Create(East()).Version);
    }

    [Fact]
    public void Version_IgnoresLinkAliasOrderAndSpelling()
    {
        string version = Create(East("https://a.example/fhir", "https://b.example/fhir")).Version;

        Create(East("https://B.example:443/fhir/", "https://a.example/fhir")).Version.ShouldBe(version);
    }

    [Fact]
    public void Version_ChangesWithLinkAliasValue()
    {
        Create(East("https://a.example/fhir")).Version.ShouldNotBe(Create(East("https://b.example/fhir")).Version);
    }

    [Fact]
    public void Version_ChangesWithShardOrder()
    {
        Create(East(), West()).Version.ShouldNotBe(Create(West(), East()).Version);
    }

    [Fact]
    public void Version_IsTheDigestOfEveryShard()
    {
        ShardRegistry registry = Create(East(), West());

        registry.ComputeVersion([ShardId.Parse("west"), ShardId.Parse("east")]).ShouldBe(registry.Version);
        registry.ComputeVersion([ShardId.Parse("east")]).ShouldNotBe(registry.Version);
    }

    [Fact]
    public void SubsetVersion_IsUnaffectedByAnUnrelatedShard()
    {
        ShardOptions otherWest = West();
        otherWest.BaseUrl = "https://west-moved.example/fhir";
        ShardRegistry before = Create(East(), West());
        ShardRegistry after = Create(East(), otherWest);

        after.Version.ShouldNotBe(before.Version);
        after.ComputeVersion([ShardId.Parse("east")]).ShouldBe(before.ComputeVersion([ShardId.Parse("east")]));
    }

    [Fact]
    public void SubsetVersion_TreatsParticipantsAsASet()
    {
        ShardRegistry registry = Create(East(), West(), Shard("north", "n1", "https://north.example"));

        registry.ComputeVersion([ShardId.Parse("north"), ShardId.Parse("east"), ShardId.Parse("north")])
            .ShouldBe(registry.ComputeVersion([ShardId.Parse("east"), ShardId.Parse("north")]));
    }

    [Fact]
    public void SubsetVersion_OfAnUnknownShard_IsRejected()
    {
        ShardRegistry registry = Create(East());

        Should.Throw<ArgumentException>(() => registry.ComputeVersion([ShardId.Parse("east"), ShardId.Parse("west")]));
        Should.Throw<ArgumentException>(() => registry.ComputeVersion([null!])).ParamName.ShouldBe("participating");
        Should.Throw<ArgumentNullException>(() => registry.ComputeVersion(null!)).ParamName.ShouldBe("participating");
    }

    private static ShardOptions Changed(string change)
    {
        ShardOptions east = East();
        switch (change)
        {
            case "Id": east.Id = "east2"; break;
            case "Alias": east.Alias = "k7q3"; break;
            case "BaseUrl": east.BaseUrl = "https://east.example/fhir2"; break;
            case "BaseUrl path case": east.BaseUrl = "https://east.example/FHIR"; break;
            case "LinkBaseAliases added": east.LinkBaseAliases.Add("https://east-private.example/fhir"); break;
            case "Profile": east.Profile = "Ignixa"; break;
            case "Auth type": east.Auth = new AuthOptions { Type = "ClientCredentials", TenantId = "t", ClientId = "c", Resource = "r", SecretName = "n" }; break;
            case "Auth client id": east.Auth!.ClientId = "another-client"; break;
            case "trailing slash": east.BaseUrl = "https://east.example/fhir/"; break;
            case "explicit :443": east.BaseUrl = "https://east.example:443/fhir"; break;
            case "host and scheme case": east.BaseUrl = "HTTPS://EAST.Example/fhir"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change), change, "Unknown change.");
        }

        return east;
    }
}

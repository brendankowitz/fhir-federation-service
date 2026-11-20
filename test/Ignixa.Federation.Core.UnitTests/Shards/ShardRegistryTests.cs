using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardRegistryTests
{
    [Fact]
    public void Shards_AreInInputOrder_WithZeroBasedOrdinals_AndSeventeenIsNotAnError()
    {
        ShardOptions[] input = [.. Enumerable.Range(0, 17).Select(i => Shard($"shard-{i}", $"s{i}", $"https://shard{i}.example/fhir"))];

        ShardRegistry registry = Create(input);

        registry.Shards.Select(s => s.Id.Value).ShouldBe(input.Select(s => s.Id));
        registry.Shards.Select(s => s.Ordinal).ShouldBe(Enumerable.Range(0, 17));
    }

    [Fact]
    public void Descriptor_CarriesTheValidatedShard()
    {
        ShardOptions east = Shard("east", "k7q2", "https://East.Example:443/fhir/", "https://east-private.example/fhir");

        ShardDescriptor shard = Create(east).Shards.ShouldHaveSingleItem();

        shard.Id.ShouldBe(ShardId.Parse("east"));
        shard.Alias.ShouldBe(ShardAlias.Parse("k7q2"));
        shard.BaseUrl.ShouldBe(new Uri("https://east.example/fhir"));
        shard.LinkBaseAliases.ShouldBe([new Uri("https://east-private.example/fhir")]);
        shard.Profile.ShouldBeSameAs(MicrosoftFhirServer);
        ManagedIdentityAuth auth = shard.Auth.ShouldBeOfType<ManagedIdentityAuth>();
        auth.Resource.ShouldBe("https://fhir.example");
        auth.ClientId.ShouldBeNull();
        shard.Ordinal.ShouldBe(0);
    }

    [Fact]
    public void MutatingTheInputAfterCreation_ChangesNothing()
    {
        ShardOptions east = East("https://east-private.example/fhir");
        var input = new List<ShardOptions> { east, West() };
        ShardRegistry registry = ShardRegistry.Create(input, Profiles);
        string version = registry.Version;

        east.Id = "changed";
        east.Alias = "changed";
        east.BaseUrl = "https://changed.example";
        east.LinkBaseAliases.Clear();
        east.Auth!.Resource = "https://changed.example";
        input.Reverse();
        input.Add(Shard("north", "n1", "https://north.example"));

        registry.Shards.Select(s => s.Id.Value).ShouldBe(["east", "west"]);
        registry.Shards[0].BaseUrl.ShouldBe(new Uri("https://east.example/fhir"));
        registry.Shards[0].LinkBaseAliases.ShouldBe([new Uri("https://east-private.example/fhir")]);
        registry.Shards[0].Auth.ShouldBeOfType<ManagedIdentityAuth>().Resource.ShouldBe("https://fhir.example");
        registry.Version.ShouldBe(version);
    }

    [Fact]
    public void Lookups_FindShardsByIdAndAlias_AndMissNothingElse()
    {
        ShardRegistry registry = Create(East(), West());

        registry.TryGetById(ShardId.Parse("west"), out ShardDescriptor? byId).ShouldBeTrue();
        byId!.Ordinal.ShouldBe(1);
        registry.TryGetByAlias(ShardAlias.Parse("k7q2"), out ShardDescriptor? byAlias).ShouldBeTrue();
        byAlias!.Id.Value.ShouldBe("east");

        registry.TryGetById(ShardId.Parse("West"), out ShardDescriptor? missingId).ShouldBeFalse();
        missingId.ShouldBeNull();
        registry.TryGetByAlias(ShardAlias.Parse("p9xn"), out ShardDescriptor? missingAlias).ShouldBeFalse();
        missingAlias.ShouldBeNull();
    }

    [Fact]
    public void Empty_IsAnError()
    {
        Errors().ShouldBe(["Federation:Shards: at least one shard is required."]);
    }

    [Fact]
    public void NullEntry_IsAnError()
    {
        SingleError("Federation:Shards:1", East(), null!);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" east")]
    [InlineData(null)]
    public void InvalidId_IsAnError(string? id)
    {
        ShardOptions shard = East();
        shard.Id = id;

        SingleError("Federation:Shards:0:Id", shard);
    }

    [Theory]
    [InlineData("K7Q2")]
    [InlineData("-k7")]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidAlias_IsAnError_AndIsNeverLowercased(string? alias)
    {
        ShardOptions shard = East();
        shard.Alias = alias;

        SingleError("Federation:Shards:0:Alias", shard);
    }

    [Fact]
    public void DuplicateId_IsAnError()
    {
        SingleError("Federation:Shards:1:Id", East(), Shard("east", "p9xm", "https://west.example/fhir"));
    }

    [Fact]
    public void IdsDifferingOnlyInCase_AreDistinct()
    {
        Create(East(), Shard("East", "p9xm", "https://west.example/fhir")).Shards.Count.ShouldBe(2);
    }

    [Fact]
    public void DuplicateAlias_IsAnError()
    {
        SingleError("Federation:Shards:1:Alias", East(), Shard("west", "k7q2", "https://west.example/fhir"));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("microsoftfhirserver")]
    [InlineData("")]
    [InlineData(null)]
    public void ProfileNotSupplied_IsAnError(string? profile)
    {
        ShardOptions shard = East();
        shard.Profile = profile;

        SingleError("Federation:Shards:0:Profile", shard);
    }

    [Fact]
    public void Profile_IsMatchedByName()
    {
        ShardOptions west = West();
        west.Profile = "Ignixa";

        Create(East(), west).Shards[1].Profile.ShouldBeSameAs(IgnixaProfile);
    }

    [Fact]
    public void AllErrors_AreCollected()
    {
        ShardOptions badAlias = East();
        badAlias.Alias = "BAD";
        ShardOptions badBaseAndProfile = West();
        badBaseAndProfile.BaseUrl = "http://west.example/fhir";
        badBaseAndProfile.Profile = "Unknown";

        IReadOnlyList<string> errors = Errors(badAlias, badBaseAndProfile, Shard("east", "n1", "https://north.example"));

        errors.Count.ShouldBe(4);
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:0:Alias: ", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:1:BaseUrl: ", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:1:Profile: ", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:2:Id: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ThrowsWithEveryError()
    {
        ShardOptions badAlias = East();
        badAlias.Alias = "BAD";
        ShardOptions badProfile = West();
        badProfile.Profile = "Unknown";

        ArgumentException ex = Should.Throw<ArgumentException>(() => Create(badAlias, badProfile));

        ex.Message.ShouldContain("Federation:Shards:0:Alias: ");
        ex.Message.ShouldContain("Federation:Shards:1:Profile: ");
    }

    [Fact]
    public void Success_ReportsNoErrors()
    {
        ShardRegistry.TryCreate([East()], Profiles, out ShardRegistry? registry, out IReadOnlyList<string> errors).ShouldBeTrue();

        registry.ShouldNotBeNull();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void InvalidArguments_AreProgrammingErrors()
    {
        Should.Throw<ArgumentNullException>(() => ShardRegistry.TryCreate(null!, Profiles, out _, out _)).ParamName.ShouldBe("shards");
        Should.Throw<ArgumentNullException>(() => ShardRegistry.TryCreate([East()], null!, out _, out _)).ParamName.ShouldBe("profiles");
        Should.Throw<ArgumentException>(() => ShardRegistry.TryCreate([East()], [MicrosoftFhirServer, null!], out _, out _));
        Should.Throw<ArgumentException>(() => ShardRegistry.TryCreate([East()], [MicrosoftFhirServer, MicrosoftFhirServer], out _, out _));
    }

    [Theory]
    [InlineData("https://fhir-east.internal/", "https://fhir-east.internal/Patient/A1")]
    [InlineData("https://east.example/fhir/", "https://east.example/fhir/Patient/A1")]
    [InlineData("https://east.example/fhir/R4", "https://east.example/fhir/R4/Patient/A1")]
    public void FullUrl_IsCanonicalBaseThenTypeThenId(string configuredBase, string expected)
    {
        ShardRegistry registry = Create(Shard("east", "k7q2", configuredBase));

        registry.FullUrl(Key("east", "Patient", "A1")).ShouldBe(expected);
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData(".")]
    [InlineData("..")]
    public void FullUrl_KeepsDotIdsVerbatim(string id)
    {
        ShardRegistry registry = Create(East());

        registry.FullUrl(Key("east", "Patient", id)).ShouldBe($"https://east.example/fhir/Patient/{id}");
    }

    [Fact]
    public void SameTypeAndIdOnTwoShards_AreTwoResourcesWithTwoUrls()
    {
        ShardRegistry registry = Create(East(), West());
        ResourceKey east = Key("east", "Patient", "A1");
        ResourceKey west = Key("west", "Patient", "A1");

        east.ShouldNotBe(west);
        registry.FullUrl(east).ShouldBe("https://east.example/fhir/Patient/A1");
        registry.FullUrl(west).ShouldBe("https://west.example/fhir/Patient/A1");
    }

    [Fact]
    public void FullUrl_OfAnUnknownShard_IsRejected()
    {
        ShardRegistry registry = Create(East());

        Should.Throw<ArgumentException>(() => registry.FullUrl(Key("west", "Patient", "A1")));
        Should.Throw<ArgumentNullException>(() => registry.FullUrl(null!));
    }

    private static ResourceKey Key(string shard, string type, string id) =>
        new(ShardId.Parse(shard), ResourceType.Parse(type), ResourceId.Parse(id));
}

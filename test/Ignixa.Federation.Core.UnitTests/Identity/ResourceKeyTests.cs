using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Identity;

public sealed class ResourceKeyTests
{
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");
    private static readonly ResourceId A1 = ResourceId.Parse("A1");

    [Fact]
    public void SameTypeAndIdOnTwoShards_AreTwoResources()
    {
        var east = new ResourceKey(ShardId.Parse("east"), Patient, A1);
        var west = new ResourceKey(ShardId.Parse("west"), Patient, A1);

        east.ShouldNotBe(west);
    }

    [Fact]
    public void Equality_IsShardTypeAndId()
    {
        var first = new ResourceKey(ShardId.Parse("east"), Patient, A1);
        var second = new ResourceKey(ShardId.Parse("east"), ResourceType.Parse("Patient"), ResourceId.Parse("A1"));

        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesTypeAndIdCase()
    {
        ShardId east = ShardId.Parse("east");
        var key = new ResourceKey(east, Patient, A1);

        key.ShouldNotBe(new ResourceKey(east, ResourceType.Parse("Group"), A1));
        key.ShouldNotBe(new ResourceKey(east, Patient, ResourceId.Parse("a1")));
    }

    [Fact]
    public void Constructor_RejectsNullParts()
    {
        ShardId east = ShardId.Parse("east");

        Should.Throw<ArgumentNullException>(() => new ResourceKey(null!, Patient, A1));
        Should.Throw<ArgumentNullException>(() => new ResourceKey(east, null!, A1));
        Should.Throw<ArgumentNullException>(() => new ResourceKey(east, Patient, null!));
    }

    [Fact]
    public void SourceShardUrl_IsTheDocumentedExtensionUrl()
    {
        FederationExtensions.SourceShardUrl.ShouldBe("http://ignixa.io/federation/StructureDefinition/source-shard");
    }
}

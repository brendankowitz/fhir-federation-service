using System.Text;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardEntryTests
{
    private static readonly ResourceKey Key = new(ShardId.Parse("east"), ResourceType.Parse("Patient"), ResourceId.Parse("A1"));

    [Fact]
    public void Resource_IsCopiedSoReusingTheSourceBufferCannotChangeIt()
    {
        byte[] exact = Encoding.UTF8.GetBytes("{ \"resourceType\":\"Patient\", \"id\":\"A1\", \"name\":[{\"text\":\"S\\u0061rah\"}] }");
        byte[] pooled = new byte[exact.Length + 16];
        exact.CopyTo(pooled, 8);

        var entry = new ShardEntry(Key, "3", null, pooled.AsSpan(8, exact.Length));
        Array.Fill(pooled, (byte)'x');

        entry.Resource.ToArray().ShouldBe(exact);
    }

    [Fact]
    public void KeepsIdentityAndOptionalMetadata()
    {
        var lastUpdated = new DateTimeOffset(2026, 11, 19, 8, 30, 0, 123, TimeSpan.Zero);

        var withMeta = new ShardEntry(Key, "7", lastUpdated, "{}"u8);
        var withoutMeta = new ShardEntry(Key, null, null, "{}"u8);

        withMeta.Key.ShouldBe(Key);
        withMeta.VersionId.ShouldBe("7");
        withMeta.LastUpdated.ShouldBe(lastUpdated);
        withoutMeta.VersionId.ShouldBeNull();
        withoutMeta.LastUpdated.ShouldBeNull();
    }

    [Fact]
    public void RejectsEmptyResourceOrMissingKey()
    {
        Should.Throw<ArgumentException>(() => new ShardEntry(Key, null, null, ReadOnlySpan<byte>.Empty));
        Should.Throw<ArgumentNullException>(() => new ShardEntry(null!, null, null, "{}"u8));
    }
}

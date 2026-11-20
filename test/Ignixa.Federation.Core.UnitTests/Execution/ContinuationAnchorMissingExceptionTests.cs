using System.Reflection;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>A missing continuation anchor always names its shard, and nothing else (design spec §7.4).</summary>
public sealed class ContinuationAnchorMissingExceptionTests
{
    [Fact]
    public void EveryConstructor_RequiresTheShard()
    {
        ConstructorInfo[] constructors = typeof(ContinuationAnchorMissingException).GetConstructors();

        constructors.ShouldNotBeEmpty();
        constructors.ShouldAllBe(c => c.GetParameters().Any(p => p.ParameterType == typeof(ShardId)));
    }

    [Fact]
    public void Shard_IsDeclaredNonNullable()
    {
        PropertyInfo shard = typeof(ContinuationAnchorMissingException).GetProperty(nameof(ContinuationAnchorMissingException.Shard))!;

        new NullabilityInfoContext().Create(shard).ReadState.ShouldBe(NullabilityState.NotNull);
    }

    [Fact]
    public void Construction_KeepsTheShardAndNamesOnlyIt()
    {
        var missing = new ContinuationAnchorMissingException(Fixture.Shard(2));

        missing.Shard.ShouldBe(Fixture.Shard(2));
        missing.InnerException.ShouldBeNull();
        missing.Message.ShouldBe("The continuation anchor is missing from the replayed page of shard 's2'.");
    }

    [Fact]
    public void NullShard_IsRejected() =>
        Should.Throw<ArgumentNullException>(() => new ContinuationAnchorMissingException(null!)).ParamName.ShouldBe("shard");
}

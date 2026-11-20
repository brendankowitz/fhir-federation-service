using System.Reflection;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>A shard failure always names its shard and reason, and nothing else (design spec §8).</summary>
public sealed class ShardFailureExceptionTests
{
    [Fact]
    public void EveryConstructor_RequiresTheShard()
    {
        ConstructorInfo[] constructors = typeof(ShardFailureException).GetConstructors();

        constructors.ShouldNotBeEmpty();
        constructors.ShouldAllBe(c => c.GetParameters().Any(p => p.ParameterType == typeof(ShardId)));
    }

    [Fact]
    public void Shard_IsDeclaredNonNullable()
    {
        PropertyInfo shard = typeof(ShardFailureException).GetProperty(nameof(ShardFailureException.Shard))!;

        new NullabilityInfoContext().Create(shard).ReadState.ShouldBe(NullabilityState.NotNull);
    }

    [Fact]
    public void Construction_KeepsTheShardReasonAndCauseAndNamesOnlyTheShardAndReason()
    {
        var cause = new InvalidOperationException("backend said 500 for ct=secret");

        var failure = new ShardFailureException(Fixture.Shard(1), ShardFailureReason.Timeout, cause);

        failure.Shard.ShouldBe(Fixture.Shard(1));
        failure.Reason.ShouldBe(ShardFailureReason.Timeout);
        failure.InnerException.ShouldBeSameAs(cause);
        failure.Message.ShouldBe("Shard 's1' failed: Timeout.");
    }

    [Fact]
    public void NullShard_IsRejected() =>
        Should.Throw<ArgumentNullException>(() => new ShardFailureException(null!, ShardFailureReason.Failed, null))
            .ParamName.ShouldBe("shard");

    [Fact]
    public void UndefinedReason_IsRejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new ShardFailureException(Fixture.Shard(0), (ShardFailureReason)99, null))
            .ParamName.ShouldBe("reason");
}

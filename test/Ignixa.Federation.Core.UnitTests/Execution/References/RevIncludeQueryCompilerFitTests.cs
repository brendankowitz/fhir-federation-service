using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>The caller's fit check judges each completed request; one that does not fit refuses the whole search.</summary>
public sealed class RevIncludeQueryCompilerFitTests
{
    private static readonly ResourceKey[] Targets = [Key(East, "a"), Key(West, "a"), Key(East, "b")];

    private static IReadOnlyList<ShardSearchRequest> Compile(Func<ShardSearchRequest, bool> fits, int pageSize = 700)
    {
        QueryPlan plan = Plan();
        return RevIncludeQueryCompiler.Compile(plan, plan.RevIncludeOf(), Targets, [East, West], pageSize, fits);
    }

    [Fact]
    public void AsksWhetherEachExactCompletedRequestFitsAndReturnsThoseSameRequests()
    {
        var seen = new List<ShardSearchRequest>();

        IReadOnlyList<ShardSearchRequest> requests = Compile(request =>
        {
            seen.Add(request);
            return true;
        });

        seen.Count.ShouldBe(2);
        seen[0].ShouldBeSameAs(requests[0]);
        seen[1].ShouldBeSameAs(requests[1]);
        seen.Select(r => (r.Shard, r.Method, r.PageSize, r.Parameters.Count)).ShouldBe(
        [
            (East, ShardSearchMethod.PostSearch, 700, 1),
            (West, ShardSearchMethod.Get, 500, 1),
        ]);
    }

    // The ceiling is inclusive: a request exactly at it fits, and one byte more does not.
    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void RefusesTheSearchOneByteOverTheLimitOfAnyShard(int slack, bool fitsAll)
    {
        int[] limits = [.. Compile(_ => true).Select(r => r.WireBytes() + slack)];
        bool Within(ShardSearchRequest request) => request.WireBytes() <= limits[request.Shard == East ? 0 : 1];

        if (fitsAll)
        {
            Compile(Within).Select(r => r.Shard).ShouldBe([East, West]);
            return;
        }

        RevIncludeCompilationException exception = Should.Throw<RevIncludeCompilationException>(() => Compile(Within));
        exception.Reason.ShouldBe(RevIncludeCompilationReason.TooCostly);
        exception.Shard.ShouldBe(East);
    }

    [Fact]
    public void RefusesTheWholeSearchWhenOnlyTheLaterShardsRequestIsTooCostly()
    {
        var seen = new List<ShardId>();

        RevIncludeCompilationException exception = Should.Throw<RevIncludeCompilationException>(() => Compile(request =>
        {
            seen.Add(request.Shard);
            return request.Shard != West;
        }));

        exception.Reason.ShouldBe(RevIncludeCompilationReason.TooCostly);
        exception.Shard.ShouldBe(West);
        seen.ShouldBe([East, West]);
    }

    [Fact]
    public void LetsAnExceptionFromTheFitCheckPropagateUnchanged()
    {
        var failure = new InvalidOperationException("measurement failed");

        Should.Throw<InvalidOperationException>(() => Compile(_ => throw failure)).ShouldBeSameAs(failure);
    }
}

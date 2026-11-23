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

    // Each shard's ceiling is its own literal wire example, spelled out here rather than measured from the result under test:
    // east is sent a POST form body, west a GET target behind its base path, both ending in their profile's clamped count.
    private const string EastPostBody = "subject=Patient%2Fa%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fa%2CPatient%2Fb%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fb%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fa&_count=1000";
    private const string WestGetTarget = "/fhir/Observation?subject=https%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fa%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fb%2Chttps%3A%2F%2Fwest.example%2Ffhir%2FPatient%2Fa&_count=500";

    private static int Bytes(string wire) => System.Text.Encoding.UTF8.GetByteCount(wire);

    // The ceiling is inclusive: a request exactly at it fits, and one byte more does not, for each method on its own.
    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(-1, 0, "east")]
    [InlineData(0, -1, "west")]
    public void RefusesTheSearchOneByteOverTheLimitOfEitherShard(int eastSlack, int westSlack, string? refused)
    {
        ShardRegistry registry = Registry();
        QueryPlan plan = Plan(registry);
        int eastLimit = Bytes(EastPostBody) + eastSlack;
        int westLimit = Bytes(WestGetTarget) + westSlack;
        var seen = new List<ShardId>();
        bool Within(ShardSearchRequest request)
        {
            seen.Add(request.Shard);
            return request.WireBytes(registry) <= (request.Shard == East ? eastLimit : westLimit);
        }

        // A page of 99999 is clamped to each shard's own maximum, so a count passed through unclamped is longer on the wire.
        IReadOnlyList<ShardSearchRequest> Run() => RevIncludeQueryCompiler.Compile(plan, plan.RevIncludeOf(), Targets, [East, West], 99999, Within);

        if (refused is null)
        {
            Run().Select(r => (r.Shard, r.Method, r.PageSize)).ShouldBe([(East, ShardSearchMethod.PostSearch, 1000), (West, ShardSearchMethod.Get, 500)]);
            seen.ShouldBe([East, West]);
            return;
        }

        RevIncludeCompilationException exception = Should.Throw<RevIncludeCompilationException>(Run);
        exception.Reason.ShouldBe(RevIncludeCompilationReason.TooCostly);
        exception.Shard.ShouldBe(ShardId.Parse(refused));
        seen.ShouldBe(refused == "east" ? [East] : [East, West]);
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

using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>What <see cref="RevIncludeQueryCompiler"/> sends each queried shard for a planned reverse include.</summary>
public sealed class RevIncludeQueryCompilerTests
{
    private static IReadOnlyList<ShardSearchRequest> Compile(
        QueryPlan plan,
        IReadOnlyCollection<ResourceKey> targets,
        IReadOnlyCollection<ShardId> sources,
        int pageSize = 50,
        Func<ShardSearchRequest, bool>? fits = null) =>
        RevIncludeQueryCompiler.Compile(plan, plan.RevIncludeOf(), targets, sources, pageSize, fits ?? (_ => true));

    // Patient/a lives on both shards: they are two resources, so each is listed, and a repeat of one is not.
    [Fact]
    public void ListsEachDistinctTargetOnceInOwnerThenIdOrderRegardlessOfInputOrder()
    {
        ResourceKey[] targets = [Key(West, "a"), Key(East, "b"), Key(East, "a"), Key(West, "a"), Key(East, "b")];

        IReadOnlyList<ShardSearchRequest> requests = Compile(Plan(), targets, [West, East, West]);

        requests.Select(r => r.Shard).ShouldBe([East, West]);
        requests[0].Decoded().ShouldBe(
            "Patient/a,https://east.example/fhir/Patient/a,Patient/b,https://east.example/fhir/Patient/b,"
            + "https://west.example/fhir/Patient/a");
        requests[1].Decoded().ShouldBe(
            "https://east.example/fhir/Patient/a,https://east.example/fhir/Patient/b,https://west.example/fhir/Patient/a");
    }

    [Fact]
    public void SendsExactlyOneBareReferencePairForTheHopsSourceType()
    {
        IReadOnlyList<ShardSearchRequest> requests = Compile(Plan(), [Key(East, "a")], [East]);

        ShardSearchRequest request = requests.ShouldHaveSingleItem();
        request.Type.ShouldBe(Observation);
        request.Parameters.ShouldHaveSingleItem().ShouldBe(new ParameterOccurrence("subject", "Patient%2Fa%2Chttps%3A%2F%2Feast.example%2Ffhir%2FPatient%2Fa"));
    }

    [Fact]
    public void AddsNoOtherPredicateOrControlFromTheClientsQuery()
    {
        QueryPlan plan = Plan(encoded: $"gender=female&_count=5&{RevInclude}");

        ShardSearchRequest request = RevIncludeQueryCompiler.Compile(plan, plan.RevIncludeOf(), [Key(East, "a")], [East], 50, _ => true).ShouldHaveSingleItem();

        request.Parameters.Select(p => p.Key).ShouldBe(["subject"]);
    }

    // East is queried by POST and clamps to 1000, west only by GET and clamps to 500, each by its own profile.
    [Theory]
    [InlineData(200, 200, 200)]
    [InlineData(700, 700, 500)]
    [InlineData(5000, 1000, 500)]
    public void ChoosesMethodAndClampedCountByTheQueriedShardsProfile(int pageSize, int eastCount, int westCount)
    {
        IReadOnlyList<ShardSearchRequest> requests = Compile(Plan(), [Key(East, "a")], [East, West], pageSize);

        requests.Select(r => (r.Shard, r.Method, r.PageSize)).ShouldBe(
        [
            (East, ShardSearchMethod.PostSearch, eastCount),
            (West, ShardSearchMethod.Get, westCount),
        ]);
    }

    // The owners of the sources are caller evidence: no source is inferred from where the targets live.
    [Fact]
    public void QueriesASourceOwnerThatOwnsNoTarget()
    {
        ShardSearchRequest request = Compile(Plan(), [Key(East, "a")], [West]).ShouldHaveSingleItem();

        request.Shard.ShouldBe(West);
        request.Decoded().ShouldBe("https://east.example/fhir/Patient/a");
    }

    [Fact]
    public void ReturnsNothingWithoutTargetsOrSourcesAndNeverAsksWhetherItFits()
    {
        int calls = 0;
        Func<ShardSearchRequest, bool> fits = _ =>
        {
            calls++;
            return true;
        };

        Compile(Plan(), [], [East, West], fits: fits).ShouldBeEmpty();
        Compile(Plan(), [Key(East, "a")], [], fits: fits).ShouldBeEmpty();

        calls.ShouldBe(0);
    }

    [Fact]
    public void ReturnsAReadOnlyList()
    {
        IReadOnlyList<ShardSearchRequest> requests = Compile(Plan(), [Key(East, "a")], [East]);

        ((ICollection<ShardSearchRequest>)requests).IsReadOnly.ShouldBeTrue();
    }
}

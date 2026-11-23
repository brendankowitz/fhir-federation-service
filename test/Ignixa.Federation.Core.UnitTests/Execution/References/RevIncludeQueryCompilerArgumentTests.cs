using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeScenario;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>A caller's invalid arguments are refused before any request is compiled or measured.</summary>
public sealed class RevIncludeQueryCompilerArgumentTests
{
    private static Exception Refusal(
        QueryPlan plan,
        PlannedOccurrence occurrence,
        ResourceKey[] targets,
        ShardId[] sources,
        int pageSize = 50)
    {
        int calls = 0;
        Exception exception = Should.Throw<Exception>(() => RevIncludeQueryCompiler.Compile(plan, occurrence, targets, sources, pageSize, _ =>
        {
            calls++;
            return true;
        }));
        calls.ShouldBe(0);
        return exception;
    }

    [Fact]
    public void RefusesAnOccurrenceOfAnotherPlan()
    {
        QueryPlan plan = Plan();
        PlannedOccurrence foreignOccurrence = Plan().RevIncludeOf();

        Refusal(plan, foreignOccurrence, [Key(East, "a")], [East]).ShouldBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public void RefusesAnOccurrenceThatIsNotAReverseInclude()
    {
        QueryPlan plan = Plan(encoded: $"gender=female&{RevInclude}");
        PlannedOccurrence predicate = plan.Occurrences.Single(o => o.Kind == OccurrenceKind.Predicate);

        Refusal(plan, predicate, [Key(East, "a")], [East]).ShouldBeAssignableTo<ArgumentException>();
    }

    [Fact]
    public void RefusesATargetOfAnotherTypeOrWithAnUnknownOwner()
    {
        QueryPlan plan = Plan();

        Refusal(plan, plan.RevIncludeOf(), [Key(East, "a", Observation)], [East]).ShouldBeAssignableTo<ArgumentException>();
        Refusal(plan, plan.RevIncludeOf(), [Key(ShardId.Parse("north"), "a")], [East]).ShouldBeAssignableTo<ArgumentException>();
    }

    // Validation precedes the empty answer, so a bad argument is never hidden by a missing one.
    [Fact]
    public void RefusesAnUnknownSourceEvenWithoutTargets()
    {
        QueryPlan plan = Plan();

        Refusal(plan, plan.RevIncludeOf(), [Key(East, "a")], [ShardId.Parse("north")]).ShouldBeAssignableTo<ArgumentException>();
        Refusal(plan, plan.RevIncludeOf(), [], [ShardId.Parse("north")]).ShouldBeAssignableTo<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusesACountBelowOne(int pageSize)
    {
        QueryPlan plan = Plan();

        Refusal(plan, plan.RevIncludeOf(), [Key(East, "a")], [East], pageSize).ShouldBeAssignableTo<ArgumentException>();
        Refusal(plan, plan.RevIncludeOf(), [], [], pageSize).ShouldBeAssignableTo<ArgumentException>();
    }
}

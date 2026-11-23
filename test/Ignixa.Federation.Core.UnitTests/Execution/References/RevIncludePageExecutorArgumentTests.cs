using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>What <see cref="RevIncludePageExecutor"/> refuses, or answers without work, before any shard call.</summary>
public sealed class RevIncludePageExecutorArgumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RequiresAClientARegistryAFitCheckAndAtLeastOneParallelCall()
    {
        ShardRegistry registry = Registry();
        var client = new FakeShardClient();

        Should.Throw<ArgumentNullException>(() => new RevIncludePageExecutor(null!, registry, 1, _ => true));
        Should.Throw<ArgumentNullException>(() => new RevIncludePageExecutor(client, null!, 1, _ => true));
        Should.Throw<ArgumentNullException>(() => new RevIncludePageExecutor(client, registry, 1, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => new RevIncludePageExecutor(client, registry, 0, _ => true));
    }

    // An equal registry built again is still another topology instance: the plan was proven against its own.
    [Fact]
    public async Task RefusesAPlanOfAnotherRegistryBeforeAnyWork()
    {
        var backend = new RevIncludeBackend();
        int fits = 0;
        RevIncludePageExecutor executor = Executor(backend.Client, Registry(), fits: _ => ++fits > 0);

        await Should.ThrowAsync<ArgumentException>(
            executor.ExecuteAsync(Plan(Registry(), Both), [Match(East, "a")], ReadAll, 10, 10, Token));

        backend.Client.Calls.ShouldBeEmpty();
        fits.ShouldBe(0);
    }

    public static TheoryData<string> InvalidMatches => ["null", "type", "shard"];

    [Theory]
    [MemberData(nameof(InvalidMatches))]
    public async Task RefusesAMatchThatIsNotAPageResultOfThePlanBeforeAnyWork(string invalid)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardEntry? bad = invalid switch
        {
            "null" => null,
            "type" => Entry(East, Observation, "a"),
            _ => Match(ShardId.Parse("north"), "a"),
        };

        await Should.ThrowAsync<ArgumentException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Both), [Match(East, "b"), bad!], ReadAll, 10, 10, Token));

        backend.Client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RefusesMissingArguments()
    {
        ShardRegistry registry = Registry();
        RevIncludePageExecutor executor = Executor(new FakeShardClient(), registry);
        QueryPlan plan = Plan(registry, Both);

        await Should.ThrowAsync<ArgumentNullException>(executor.ExecuteAsync(null!, [], ReadAll, 10, 10, Token));
        await Should.ThrowAsync<ArgumentNullException>(executor.ExecuteAsync(plan, null!, ReadAll, 10, 10, Token));
        await Should.ThrowAsync<ArgumentNullException>(executor.ExecuteAsync(plan, [], null!, 10, 10, Token));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public async Task RefusesANegativeLimitOrAllowance(int includeLimit, int remainingFetches)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, includeLimit, remainingFetches, Token));

        backend.Client.Calls.ShouldBeEmpty();
    }

    // The caller must be able to read the searched type at all; the refusal says nothing about which type or resource.
    [Fact]
    public async Task RefusesACallerWithoutTheSearchedTypesGrantWithAGenericMessage()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();

        UnauthorizedAccessException thrown = await Should.ThrowAsync<UnauthorizedAccessException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Both), [Match(East, "a")], Readable(Observation, Encounter), 10, 10, Token));

        thrown.Message.ShouldNotContain("Patient");
        thrown.Message.ShouldNotContain("east");
        backend.Client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACountOnlyPlanHasNoMatchesAndNoIncludes()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        RevIncludePageExecutor executor = Executor(backend.Client, registry);
        QueryPlan plan = Plan(registry, $"_count=0&{Both}");

        RevIncludePage page = await executor.ExecuteAsync(plan, [], Readable(Patient), 10, 10, Token);
        await Should.ThrowAsync<ArgumentException>(executor.ExecuteAsync(plan, [Match(East, "a")], ReadAll, 10, 10, Token));

        page.Includes.ShouldBeEmpty();
        page.OmittedUnauthorized.ShouldBeFalse();
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(0);
        backend.Client.Calls.ShouldBeEmpty();
    }

    // An empty page has nothing to include, yet a forbidden reverse include is still reported as left out.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEmptyPageSearchesNothingAndReportsOnlyAForbiddenSource(bool encounterReadable)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        int fits = 0;
        IReadOnlySet<ResourceType> readable = encounterReadable ? ReadAll : Readable(Patient, Observation);

        RevIncludePage page = await Executor(backend.Client, registry, fits: _ => ++fits > 0)
            .ExecuteAsync(Plan(registry, Both), [], readable, 10, 0, Token);

        page.Includes.ShouldBeEmpty();
        page.OmittedUnauthorized.ShouldBe(!encounterReadable);
        page.Truncated.ShouldBeFalse();
        page.Fetches.ShouldBe(0);
        backend.Client.Calls.ShouldBeEmpty();
        fits.ShouldBe(0);
    }

    [Theory]
    [InlineData("_include=Patient:organization")]
    [InlineData("_count=5")]
    public async Task APlanWithoutAReverseIncludeSearchesNothing(string encoded)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();

        RevIncludePage page = await Executor(backend.Client, registry).ExecuteAsync(Plan(registry, encoded), [Match(East, "a")], ReadAll, 10, 0, Token);

        page.Includes.ShouldBeEmpty();
        page.OmittedUnauthorized.ShouldBeFalse();
        page.Fetches.ShouldBe(0);
        backend.Client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryReverseIncludeForbiddenSearchesNothing()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();

        RevIncludePage page = await Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Both), [Match(East, "a")], Readable(Patient), 10, 0, Token);

        page.Includes.ShouldBeEmpty();
        page.OmittedUnauthorized.ShouldBeTrue();
        page.Fetches.ShouldBe(0);
        backend.Client.Calls.ShouldBeEmpty();
    }
}

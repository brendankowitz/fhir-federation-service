using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>How <see cref="RevIncludePageExecutor"/> spends the request's remaining fetches and fails before overspending them.</summary>
public sealed class RevIncludePageExecutorBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Every authorized request is fitted before the first search, so a later one that does not fit sends nothing.
    [Fact]
    public async Task ALaterRequestThatDoesNotFitFailsBeforeAnySearch()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        var fitted = new List<(ShardId, ResourceType)>();

        RevIncludeCompilationException thrown = await Should.ThrowAsync<RevIncludeCompilationException>(
            Executor(backend.Client, registry, 4, request =>
            {
                fitted.Add((request.Shard, request.Type));
                return !(request.Shard == West && request.Type == Encounter);
            }).ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 10, 10, Token));

        thrown.Reason.ShouldBe(RevIncludeCompilationReason.TooCostly);
        thrown.Shard.ShouldBe(West);
        fitted.ShouldBe([(East, Observation), (West, Observation), (East, Encounter), (West, Encounter)]);
        backend.Client.Calls.ShouldBeEmpty();
    }

    // Two occurrences over two shards need at least four first pages; fewer remaining fails before compiling any.
    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task TheMandatoryFirstPagesMustFitTheRemainingFetches(int remainingFetches, bool fits)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, Page(null));
        backend.OnSearch(West, Observation, Page(null));
        backend.OnSearch(East, Encounter, Page(null));
        backend.OnSearch(West, Encounter, Page(null));
        int fitted = 0;
        RevIncludePageExecutor executor = Executor(backend.Client, registry, fits: _ => ++fitted > 0);

        Task<RevIncludePage> execution = executor.ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 10, remainingFetches, Token);

        if (fits)
        {
            (await execution).Fetches.ShouldBe(4);
            return;
        }

        FetchBudgetExhaustedException thrown = await Should.ThrowAsync<FetchBudgetExhaustedException>(execution);
        thrown.Message.ShouldNotContain("east");
        fitted.ShouldBe(0);
        backend.Client.Calls.ShouldBeEmpty();
    }

    // A next is sent only while the unstarted first pages stay affordable after it, so the first page's next is refused
    // with four fetches (one spent, three reserved) and followed with five. An empty page with a next still costs a fetch.
    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task ANextIsSentOnlyIfEveryUnstartedFirstPageStaysAffordable(int remainingFetches, bool affordable)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        backend.OnSearch(East, Observation, Page(second));
        backend.OnContinue(second, Page(null, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, Page(null));
        backend.OnSearch(East, Encounter, Page(null));
        backend.OnSearch(West, Encounter, Page(null));

        Task<RevIncludePage> execution = Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 10, remainingFetches, Token);

        if (affordable)
        {
            RevIncludePage page = await execution;
            page.Rendered().ShouldBe(["east/Observation/o1/1/"]);
            page.Fetches.ShouldBe(5);
            return;
        }

        await Should.ThrowAsync<FetchBudgetExhaustedException>(execution);
        backend.Client.Calls.ShouldHaveSingleItem().Search.ShouldNotBeNull();
    }
}

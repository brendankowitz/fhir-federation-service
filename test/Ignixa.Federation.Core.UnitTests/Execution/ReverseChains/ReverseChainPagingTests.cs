using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

public sealed class ReverseChainPagingTests
{
    private readonly ReverseChainFixture _f = new();

    [Fact]
    public async Task BudgetIsLiveAcrossContinuationsAndAcrossClauses()
    {
        _f.Client.ServePages(East, TestShards.MicrosoftFhirServer, [], [Obs("e", "Patient/a")]);
        _f.Client.OnSearch(West, Page(null));
        var plan = _f.Plan(ReverseChainFixture.Has + "=1&" + ReverseChainFixture.Has + "=2");
        (await _f.ResolveAsync(plan, 6)).Calls.ShouldBe(6);

        await Should.ThrowAsync<FetchBudgetExhaustedException>(() => _f.ResolveAsync(plan, 5));
        _f.Client.Calls.Count.ShouldBe(11);
    }

    [Theory]
    [InlineData("exhausted")]
    [InlineData("cycle")]
    [InlineData("wrong shard")]
    [InlineData("wrong type")]
    public async Task EverySourcePageMustBeFollowedAndValidated(string ending)
    {
        _f.Client.ServePages(East, TestShards.MicrosoftFhirServer, [], [Obs("last", "Patient/a")]);
        _f.Client.OnSearch(West, (request, _) =>
        {
            var next = Cursor(request, TestShards.MicrosoftFhirServer, "next");
            _f.Client.OnContinue(next, ending switch
            {
                "cycle" => Page(next),
                "wrong shard" => Page(null, Obs(East, "last", "Patient/a")),
                "wrong type" => Page(null, Pat(West, "b")),
                _ => Page(null, Obs(West, "last", "Patient/b")),
            });
            return Task.FromResult(Page(next));
        });
        var resolve = _f.Resolver(rows: 2).ResolveAsync(_f.Plan(), AllTypes, 4, TestContext.Current.CancellationToken);
        if (ending == "exhausted")
        {
            var result = await resolve;
            result.Calls.ShouldBe(4);
            result.Targets.Select(t => t.Shard).ShouldBe([East, West]);
            result.Targets.Select(t => t.Replacements[0]).ShouldBe(["a", "b"]);
            _f.Client.Calls.Where(c => c.Search is not null).Select(c => c.Search!.PageSize).ShouldBe([3, 3]);
        }
        else
        {
            var exception = await Should.ThrowAsync<ShardFailureException>(resolve);
            exception.Shard.ShouldBe(West);
            exception.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        }
    }
}

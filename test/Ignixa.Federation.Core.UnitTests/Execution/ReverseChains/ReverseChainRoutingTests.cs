using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Search;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;
using static Ignixa.Federation.Core.UnitTests.Execution.ReverseChains.ReverseChainFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

public sealed class ReverseChainRoutingTests
{
    [Theory]
    [InlineData(Has + "=1&" + Has + "=2", "_id=a&_id=a", ShardSearchMethod.Get)]
    [InlineData(Has + "=1&_id=a", "_id=a&_id=a", ShardSearchMethod.Get)]
    [InlineData(Has + "=1", "_id=a", ShardSearchMethod.PostSearch)]
    [InlineData(Has + "=1&_id=b", "_id=a&_id=b", ShardSearchMethod.PostSearch)]
    [InlineData(Has + "=1&name=a", "_id=a&name=a", ShardSearchMethod.PostSearch)]
    public async Task PostCapableTargetUsesGetOnlyForIdenticalPreparedPairs(string query, string wire, ShardSearchMethod method)
    {
        var fixture = new ReverseChainFixture();
        fixture.Client.OnSearch(East, Page(null, Obs("witness", "Patient/a")));
        fixture.Client.OnSearch(West, Page(null));

        var result = await fixture.ResolveAsync(fixture.Plan(query));
        var target = result.Targets.ShouldHaveSingleItem();
        QueryRewriter.Render(target.Parameters).ShouldBe(wire);
        target.Method.ShouldBe(method);
        target.Request(5).Method.ShouldBe(method);
    }

    [Theory]
    [InlineData("overlap", 4)]
    [InlineData("different owners", 4)]
    [InlineData("empty first", 2)]
    public async Task RepeatedPredicatesIntersectPhysicalPatientKeysNotObservationIds(string scenario, int calls)
    {
        var fixture = new ReverseChainFixture();
        fixture.Client.OnSearch(East, (request, _) => Task.FromResult(
            request.Parameters.Single().Value != "1" ? Page(null) : scenario switch
            {
                "overlap" => Page(null, Obs("o1", "Patient/b"), Obs("o2", "Patient/a"), Obs("o3", "Patient/b"), Obs("o4", "Patient/first")),
                "different owners" => Page(null, Obs("o1", "Patient/dup")),
                _ => Page(null),
            }));
        fixture.Client.OnSearch(West, (request, _) => Task.FromResult(
            request.Parameters.Single().Value == "1" ? Page(null) : scenario == "overlap"
                ? Page(null, Obs(West, "o5", "https://east.example/fhir/Patient/a"),
                    Obs(West, "o6", "https://east.example/fhir/Patient/b"), Obs(West, "o7", "Patient/second"))
                : Page(null, Obs(West, "o8", "Patient/dup"))));

        var result = await fixture.ResolveAsync(fixture.Plan(Has + "=1&name=x&" + Has + "=2"));
        result.Calls.ShouldBe(calls);
        fixture.Client.Calls.Count.ShouldBe(calls);
        if (scenario == "overlap")
        {
            var target = result.Targets.ShouldHaveSingleItem();
            target.Shard.ShouldBe(East);
            target.Replacements[0].ShouldBe("b,a");
            target.Replacements[2].ShouldBe("b,a");
            QueryRewriter.Render(target.Parameters).ShouldBe("_id=b%2Ca&name=x&_id=b%2Ca");
        }
        else
        {
            result.Targets.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(true, "Patient/a", "east", "a", false)]
    [InlineData(false, "Patient/a/_history/2", "west", "a", false)]
    [InlineData(false, "https://east.example/fhir/Patient/dup", "east", "dup", false)]
    [InlineData(true, "https://west.example/fhir/Patient/dup", "west", "dup", true)]
    [InlineData(false, "https://foreign.example/Patient/a", null, null, false)]
    [InlineData(true, "#x", null, null, false)]
    [InlineData(false, "Group/g", null, null, false)]
    [InlineData(false, null, null, null, false)]
    public async Task PrimarySearchUsesOnlyTheOwnerOfATypedRoutableWitness(
        bool fromEast, string? literal, string? owner, string? id, bool getOnly)
    {
        var fixture = new ReverseChainFixture
        {
            Capabilities = TestSearch.Capabilities(registry: getOnly ? Registry(GetOnly) : Registry()),
        };
        ShardId source = fromEast ? East : West;
        fixture.Client.OnSearch(source, Page(null, Obs(source, "obs", literal)));
        fixture.Client.OnSearch(fromEast ? West : East, Page(null));

        var result = await fixture.ResolveAsync(fixture.Plan(Has + "=123&name=x"));
        result.Calls.ShouldBe(2);
        fixture.Client.Calls.Count.ShouldBe(2);
        foreach (FakeCall call in fixture.Client.Calls)
        {
            call.Search!.Type.ShouldBe(Observation);
            call.Search.Method.ShouldBe(getOnly ? ShardSearchMethod.Get : ShardSearchMethod.PostSearch);
            QueryRewriter.Render(call.Search.Parameters).ShouldBe("code=123");
        }

        if (owner is null)
        {
            result.Targets.ShouldBeEmpty();
            return;
        }

        var target = result.Targets.ShouldHaveSingleItem();
        target.Shard.Value.ShouldBe(owner);
        target.Method.ShouldBe(getOnly ? ShardSearchMethod.Get : ShardSearchMethod.PostSearch);
        target.Replacements[0].ShouldBe(id);
        QueryRewriter.Render(target.Parameters).ShouldBe($"_id={id}&name=x");

        // Both stores have the id: querying the wrong shard would return a real, unrelated patient.
        fixture.Client.OnSearch(East, Page(null, Pat(East, id!)));
        fixture.Client.OnSearch(West, Page(null, Pat(West, id!)));
        var primary = new UnsortedPageExecutor(fixture.Client, fixture.Capabilities.Registry, 2);
        var page = await primary.ExecuteAsync([.. result.Targets.Select(t => t.Request(5))], 5, null, 2, TestContext.Current.CancellationToken);
        page.Matches.ShouldHaveSingleItem().Key.ShouldBe(Key(ShardId.Parse(owner), Patient, id!));
        fixture.Client.Calls.Skip(result.Calls).ShouldHaveSingleItem().Shard.Value.ShouldBe(owner);
    }
}

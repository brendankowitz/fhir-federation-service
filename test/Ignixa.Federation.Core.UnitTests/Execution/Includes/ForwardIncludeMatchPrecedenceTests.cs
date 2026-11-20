using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>
/// A match takes precedence over an include of the same resource, identified by shard, type and id (spec §4.1, §7.6): it is
/// never requested, so it is never repeated as an include, whatever version a read would return.
/// </summary>
public sealed class ForwardIncludeMatchPrecedenceTests
{
    [Fact]
    public async Task A_target_that_is_a_match_on_the_page_is_neither_requested_nor_included()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Pat(East, "p1")],
            ObservationSubject);

        client.Searches().ShouldBe(["east Patient?_id=p2"]);
        result.Rendered().ShouldBe(["east/Patient/p2@1"]);
    }

    [Fact]
    public async Task A_backend_row_for_a_match_is_unrequested_so_any_version_of_it_breaks_the_contract()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, Page(null, Pat(East, "p2"), Pat(East, "p1", "2")));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() => Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Pat(East, "p1")],
            ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        RequestedIds(client.Calls.ShouldHaveSingleItem().Search!).ShouldBe(["p2"]);
    }

    [Fact]
    public async Task Self_and_peer_references_among_matches_include_nothing_and_make_no_calls()
    {
        var client = new FakeShardClient();

        IncludeResult result = await Resolver(client).ResolveAsync(
            [
                Panel(East, "o1", "Observation/o1", "Observation/o2"),
                Panel(East, "o2", "https://east.example/fhir/Observation/o1"),
            ],
            [ObservationHasMember],
            AllTypes,
            0,
            TestContext.Current.CancellationToken);

        result.Includes.ShouldBeEmpty();
        result.Outcomes.ShouldBeEmpty();
        result.Fetches.ShouldBe(0);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_versioned_reference_to_a_match_is_reported_but_not_fetched()
    {
        var client = new FakeShardClient();

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Panel(East, "o1", "Observation/o2/_history/9"), Panel(East, "o2")],
            ObservationHasMember);

        result.Includes.ShouldBeEmpty();
        result.Outcomes.Select(o => (o.Kind, o.Path)).ShouldBe([(IncludeOutcomeKind.VersionedReference, ObservationHasMember.Hop!.Path)]);
        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_id_on_another_shard_is_another_resource_so_it_is_fetched_while_the_match_is_not_repeated()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Panel(East, "o1"));
        client.ServeIds(West, Panel(West, "o1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Panel(East, "o1"), Panel(East, "o2", "Observation/o1"), Panel(West, "w1", "Observation/o1")],
            ObservationHasMember);

        client.Searches().ShouldBe(["west Observation?_id=o1"]);
        result.Rendered().ShouldBe(["west/Observation/o1@1"]);
        result.Outcomes.ShouldBeEmpty();
    }
}

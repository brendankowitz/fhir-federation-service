using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>How a page's includes are deduplicated and capped, and what proves the cap was exceeded (spec §7.6).</summary>
public sealed class ForwardIncludeOverflowTests
{
    [Fact]
    public async Task A_resource_repeated_on_a_later_backend_page_is_emitted_once()
    {
        var client = new FakeShardClient();
        client.ServePages(East, TestShards.MicrosoftFhirServer, [Pat(East, "p1")], [Pat(East, "p1"), Pat(East, "p2")]);

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
        result.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task Exactly_the_limit_is_not_truncation()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"));

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 2).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
        result.Outcomes.ShouldBeEmpty();
        client.Calls.Single().Search!.PageSize.ShouldBe(2);
    }

    [Fact]
    public async Task A_distinct_resource_beyond_the_limit_proves_truncation()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"), Pat(East, "p3"));

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 2).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs("o3", "Patient/p3")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
        IncludeOutcome truncated = result.Outcomes.ShouldHaveSingleItem();
        truncated.Kind.ShouldBe(IncludeOutcomeKind.Truncated);
        truncated.Path.ShouldBeNull();
        client.Calls.Single().Search!.PageSize.ShouldBe(3);
    }

    [Fact]
    public async Task Overflow_stops_the_fetch_so_later_groups_are_never_called()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"));

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 1).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs(West, "w1", "Patient/p3")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1"]);
        result.Outcomes.Select(o => o.Kind).ShouldBe([IncludeOutcomeKind.Truncated]);
        client.Searches().ShouldBe(["east Patient?_id=p1,p2"]);
    }

    [Fact]
    public async Task A_later_group_asks_only_for_the_free_slots_plus_one_and_can_prove_overflow()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));
        client.ServeIds(West, Pat(West, "p2"), Pat(West, "p3"), Pat(West, "p4"));

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 2).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs(West, "w2", "Patient/p2"), Obs(West, "w3", "Patient/p3"), Obs(West, "w4", "Patient/p4")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "west/Patient/p2@1"]);
        result.Outcomes.Select(o => o.Kind).ShouldBe([IncludeOutcomeKind.Truncated]);
        client.Calls.Select(c => c.Search!.PageSize).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Overflow_proved_on_a_followed_page_is_truncation()
    {
        var client = new FakeShardClient();
        client.ServePages(East, TestShards.MicrosoftFhirServer, [Pat(East, "p1")], [Pat(East, "p2")]);

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 1).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1"]);
        result.Outcomes.Select(o => o.Kind).ShouldBe([IncludeOutcomeKind.Truncated]);
        result.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task A_match_does_not_count_toward_the_limit()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"), Pat(East, "p2"));

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 1).ResolveAsync(
            [Pat(East, "p1"), Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p2@1"]);
        result.Outcomes.ShouldBeEmpty();
        client.Searches().ShouldBe(["east Patient?_id=p2"]);
    }

    [Fact]
    public async Task A_next_link_and_a_larger_total_do_not_prove_overflow()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (request, _) =>
        {
            ShardCursor next = Cursor(request, TestShards.MicrosoftFhirServer, "more");
            client.OnContinue(next, Page(null));
            return Task.FromResult(new ShardPage([Pat(East, "p1")], next, 50));
        });

        IncludeResult result = await Resolver(client, maxIncludesPerPage: 1).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1"]);
        result.Outcomes.ShouldBeEmpty();
        result.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task An_unrequested_row_after_the_overflow_witness_still_fails_the_page()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, Page(null, Pat(East, "p1"), Pat(East, "p2"), Pat(East, "x9")));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client, maxIncludesPerPage: 1).ResolveAsync([Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")], ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }
}

using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>The <c>_id</c> searches a forward include sends, how it pages them, and which answers break the contract.</summary>
public sealed class ForwardIncludeFetchTests
{
    [Fact]
    public async Task A_backend_that_supports_it_is_searched_with_post()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));

        await Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject);

        client.Calls.Single().Search!.Method.ShouldBe(ShardSearchMethod.PostSearch);
    }

    [Fact]
    public async Task A_get_only_backend_is_searched_and_followed_with_get()
    {
        var client = new FakeShardClient();
        client.ServePages(East, GetOnly, [Pat(East, "p1")], [Pat(East, "p2")]);

        IncludeResult result = await Resolver(client, Registry(GetOnly)).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        client.Calls[0].Search!.Method.ShouldBe(ShardSearchMethod.Get);
        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
    }

    [Fact]
    public async Task The_page_size_is_capped_by_the_backend_and_next_links_are_followed_to_the_end()
    {
        var client = new FakeShardClient();
        client.ServePages(East, Fixture.SmallPages, [Pat(East, "p1"), Pat(East, "p2")], [Pat(East, "p3")]);

        IncludeResult result = await Resolver(client, Registry(Fixture.SmallPages)).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs("o3", "Patient/p3")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1", "east/Patient/p3@1"]);
        client.Calls[0].Search!.PageSize.ShouldBe(2);
        client.Calls[1].Cursor.ShouldNotBeNull();
        result.Fetches.ShouldBe(2);
    }

    [Fact]
    public async Task Empty_and_short_pages_with_a_next_link_are_followed()
    {
        var client = new FakeShardClient();
        client.ServePages(East, TestShards.MicrosoftFhirServer, [], [Pat(East, "p1")], [], [Pat(East, "p2")]);

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1", "east/Patient/p2@1"]);
        result.Fetches.ShouldBe(4);
    }

    [Fact]
    public async Task A_missing_target_is_not_an_error()
    {
        var client = new FakeShardClient();
        client.ServeIds(East, Pat(East, "p1"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/gone")],
            ObservationSubject);

        result.Rendered().ShouldBe(["east/Patient/p1@1"]);
        result.Outcomes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("west", "Patient", "p1")]
    [InlineData("east", "Organization", "p1")]
    [InlineData("east", "Patient", "x9")]
    public async Task A_row_of_another_shard_type_or_id_breaks_the_contract(string shard, string type, string id)
    {
        var client = new FakeShardClient();
        client.OnSearch(East, Page(null, Resource(ShardId.Parse(shard), ResourceType.Parse(type), id, "1")));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Fact]
    public async Task A_row_repeated_on_one_page_breaks_the_contract()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, Page(null, Pat(East, "p1"), Pat(East, "p1")));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject));

        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
    }

    [Fact]
    public async Task A_next_link_that_repeats_a_redeemed_cursor_breaks_the_contract()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (request, _) =>
        {
            ShardCursor next = Cursor(request, TestShards.MicrosoftFhirServer, "loop");
            client.OnContinue(next, Page(next));
            return Task.FromResult(Page(next, Pat(East, "p1")));
        });

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.ContractViolation);
        client.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Fetches_counts_every_search_and_follow_across_groups()
    {
        var client = new FakeShardClient();
        client.ServePages(East, TestShards.MicrosoftFhirServer, [Pat(East, "p1")], [Pat(East, "p2")]);
        client.ServeIds(West, Pat(West, "p3"));

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2"), Obs(West, "w3", "Patient/p3")],
            ObservationSubject);

        result.Fetches.ShouldBe(3);
        client.Calls.Count.ShouldBe(3);
    }
}

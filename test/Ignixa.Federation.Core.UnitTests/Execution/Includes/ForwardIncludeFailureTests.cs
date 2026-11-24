using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Search;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.Includes;

/// <summary>How a forward include fails: shard faults, budget, cancellation and invalid arguments.</summary>
public sealed class ForwardIncludeFailureTests
{
    [Fact]
    public async Task A_client_fault_fails_the_target_shard()
    {
        var client = new FakeShardClient();
        client.OnSearch(West, (_, _) => Task.FromException<ShardPage>(new InvalidOperationException("down")));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client, Registry("https://west-alias.example/fhir")).ResolveAsync(
                [Obs("o1", "https://west-alias.example/fhir/Patient/p1")],
                ObservationSubject));

        failure.Shard.ShouldBe(West);
        failure.Reason.ShouldBe(ShardFailureReason.Failed);
    }

    [Fact]
    public async Task A_client_cancellation_the_caller_did_not_ask_for_is_a_timeout()
    {
        var client = new FakeShardClient();
        client.OnSearch(East, (_, _) => Task.FromCanceled<ShardPage>(new CancellationToken(canceled: true)));

        ShardFailureException failure = await Should.ThrowAsync<ShardFailureException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject));

        failure.Shard.ShouldBe(East);
        failure.Reason.ShouldBe(ShardFailureReason.Timeout);
    }

    [Fact]
    public async Task A_cancelled_caller_is_never_called_for()
    {
        var client = new FakeShardClient();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], [ObservationSubject], AllTypes, 10, cancelled.Token));

        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Caller_cancellation_during_a_call_is_cancellation()
    {
        var client = new FakeShardClient();
        using var caller = new CancellationTokenSource();
        client.OnSearch(East, async (_, ct) =>
        {
            await caller.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Page(null, Pat(East, "p1"));
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], [ObservationSubject], AllTypes, 10, caller.Token));
    }

    [Fact]
    public async Task Caller_cancellation_during_a_call_that_still_returns_a_page_is_cancellation()
    {
        var client = new FakeShardClient();
        using var caller = new CancellationTokenSource();
        client.OnSearch(East, async (_, _) =>
        {
            await caller.CancelAsync();
            return Page(null, Pat(East, "p1"));
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], [ObservationSubject], AllTypes, 10, caller.Token));

        client.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_follow_beyond_the_budget_exhausts_it()
    {
        var client = new FakeShardClient();
        client.ServePages(East, TestShards.MicrosoftFhirServer, [Pat(East, "p1")], [Pat(East, "p2")]);

        await Should.ThrowAsync<FetchBudgetExhaustedException>(() => Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1"), Obs("o2", "Patient/p2")],
            [ObservationSubject],
            AllTypes,
            1,
            TestContext.Current.CancellationToken));

        client.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_zero_budget_with_a_target_to_fetch_is_exhausted_without_a_call()
    {
        var client = new FakeShardClient();

        await Should.ThrowAsync<FetchBudgetExhaustedException>(() => Resolver(client).ResolveAsync(
            [Obs("o1", "Patient/p1")], [ObservationSubject], AllTypes, 0, TestContext.Current.CancellationToken));

        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_zero_budget_with_nothing_to_fetch_succeeds()
    {
        var client = new FakeShardClient();

        IncludeResult result = await Resolver(client).ResolveAsync(
            [Obs(East, "o1", null)], [ObservationSubject], AllTypes, 0, TestContext.Current.CancellationToken);

        result.Includes.ShouldBeEmpty();
        result.Fetches.ShouldBe(0);
    }

    [Theory]
    [InlineData("_revinclude=Observation:subject")]
    [InlineData("name=smith")]
    public async Task An_occurrence_that_is_not_a_forward_include_is_rejected_before_any_call(string query)
    {
        var client = new FakeShardClient();
        PlannedOccurrence occurrence = TestSearch.PlanOk("Patient", query).Occurrences.Single();

        await Should.ThrowAsync<ArgumentException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1")], ObservationSubject, occurrence));

        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_null_match_is_rejected_before_any_call()
    {
        var client = new FakeShardClient();

        await Should.ThrowAsync<ArgumentException>(() =>
            Resolver(client).ResolveAsync([Obs("o1", "Patient/p1"), null!], ObservationSubject));

        client.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_rejects_null_arguments_and_a_negative_budget()
    {
        ForwardIncludeResolver resolver = Resolver(new FakeShardClient());
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentNullException>(() => resolver.ResolveAsync(null!, [ObservationSubject], AllTypes, 1, ct));
        await Should.ThrowAsync<ArgumentNullException>(() => resolver.ResolveAsync([], null!, AllTypes, 1, ct));
        await Should.ThrowAsync<ArgumentNullException>(() => resolver.ResolveAsync([], [ObservationSubject], null!, 1, ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => resolver.ResolveAsync([], [ObservationSubject], AllTypes, -1, ct));
    }

    [Fact]
    public void The_constructor_rejects_null_dependencies_and_a_limit_below_one()
    {
        var client = new FakeShardClient();
        ShardRegistry registry = Registry();

        Should.Throw<ArgumentNullException>(() => new ForwardIncludeResolver(null!, registry, 1, TestSearch.R4Specification));
        Should.Throw<ArgumentNullException>(() => new ForwardIncludeResolver(client, null!, 1, TestSearch.R4Specification));
        Should.Throw<ArgumentNullException>(() => new ForwardIncludeResolver(client, registry, 1, null!));
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardIncludeResolver(client, registry, 0, TestSearch.R4Specification));
    }
}

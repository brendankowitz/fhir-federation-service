using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.References.RevIncludeTraversal;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// Bounded first-page overlap, joining every sibling call, ordinal failure arbitration and caller cancellation. Gates
/// order every interleaving; timeouts only bound failed waits.
/// </summary>
public sealed class RevIncludePageExecutorConcurrencyTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // First pages overlap up to the bound within one occurrence, never across occurrences, and the includes keep plan
    // and owner order however the calls complete.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FirstPagesOverlapWithinTheBoundAndIncludesKeepTheirOrder(int maxParallelFetches)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        TaskCompletionSource eastObservation = new(), westObservation = new(), eastEncounter = new(), westEncounter = new();
        backend.OnSearch(East, Observation, eastObservation, Page(null, Entry(East, Observation, "o1")));
        backend.OnSearch(West, Observation, westObservation, Page(null, Entry(West, Observation, "o2")));
        backend.OnSearch(East, Encounter, eastEncounter, Page(null, Entry(East, Encounter, "e1")));
        backend.OnSearch(West, Encounter, westEncounter, Page(null, Entry(West, Encounter, "e2")));

        Task<RevIncludePage> execution = Executor(backend.Client, registry, maxParallelFetches)
            .ExecuteAsync(Plan(registry, Both), [Match(East, "a")], ReadAll, 10, 4, Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        for (int yields = 0; yields < 10; yields++)
        {
            backend.Client.Calls.Count.ShouldBe(2);
            await Task.Yield();
        }

        westObservation.SetResult();
        eastObservation.SetResult();
        await backend.Client.WhenStarted(4).WaitAsync(GateTimeout, Token);
        westEncounter.SetResult();
        eastEncounter.SetResult();
        RevIncludePage page = await execution;

        backend.Client.Calls.Select(call => (call.Shard, call.Search!.Type)).ShouldBe(
            [(East, Observation), (West, Observation), (East, Encounter), (West, Encounter)]);
        backend.Client.MaxInFlight.ShouldBe(2);
        page.Rendered().ShouldBe(["east/Observation/o1/1/", "west/Observation/o2/1/", "east/Encounter/e1/1/", "west/Encounter/e2/1/"]);
    }

    [Fact]
    public async Task AFailedFirstPageWaitsForItsSiblingBeforeFailingTheCall()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        var release = new TaskCompletionSource();
        backend.OnSearchThrows(East, Observation, new InvalidOperationException("east is down"));
        backend.OnSearch(West, Observation, release, Page(null));

        Task<RevIncludePage> execution = Executor(backend.Client, registry, 2)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 2, Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        await backend.Client.Calls[0].Finished.WaitAsync(GateTimeout, Token);

        // West's call is parked on release, so a joined call cannot have ended. Yielding gives an unjoined one time to end.
        for (int yields = 0; yields < 10; yields++)
        {
            execution.IsCompleted.ShouldBeFalse();
            await Task.Yield();
        }

        release.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);

        thrown.Shard.ShouldBe(East);
        thrown.Reason.ShouldBe(ShardFailureReason.Failed);
        backend.Client.Calls.ShouldAllBe(call => call.Finished.IsCompleted);
    }

    // The lowest-ordinal failure of a batch wins whether it is the call's or the page's, and whichever completes first.
    [Theory]
    [InlineData("invalid", "throws", "east", ShardFailureReason.ContractViolation)]
    [InlineData("throws", "invalid", "east", ShardFailureReason.Failed)]
    [InlineData("valid", "invalid", "west", ShardFailureReason.ContractViolation)]
    public async Task TheLowestOrdinalFailureOfABatchWins(string east, string west, string failed, ShardFailureReason reason)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        var eastReleased = new TaskCompletionSource();
        backend.OnSearch(East, Observation, async (_, cancellationToken) =>
        {
            await eastReleased.Task.WaitAsync(cancellationToken);
            return Outcome(East, east);
        });
        backend.OnSearch(West, Observation, (_, _) => Task.FromResult(Outcome(West, west)));

        Task<RevIncludePage> execution = Executor(backend.Client, registry, 2)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 2, Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        await backend.Client.Calls[1].Finished.WaitAsync(GateTimeout, Token);
        eastReleased.SetResult();
        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(execution);

        thrown.Shard.ShouldBe(failed == "east" ? East : West);
        thrown.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task CallerCancellationDuringAFirstPageBatchWins()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        backend.OnSearch(East, Observation, new TaskCompletionSource(), Page(null));
        backend.OnSearch(West, Observation, new TaskCompletionSource(), Page(null));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<RevIncludePage> execution = Executor(backend.Client, registry, 2)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 2, caller.Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        await caller.CancelAsync();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        backend.Client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task CallerCancellationBeforeArbitrationWinsOverAViolation()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        var release = new TaskCompletionSource();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        backend.OnSearch(East, Observation, async (_, _) =>
        {
            await release.Task;
            await caller.CancelAsync();
            return Outcome(East, "invalid");
        });
        backend.OnSearch(West, Observation, Page(null));

        Task<RevIncludePage> execution = Executor(backend.Client, registry, 2)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 2, caller.Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        release.SetResult();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
    }

    [Fact]
    public async Task CallerCancellationStopsFurtherFirstPageAdmissionsButJoinsTheStartedCall()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        backend.OnSearch(East, Observation, async (_, _) =>
        {
            await caller.CancelAsync();
            await release.Task.WaitAsync(Token);
            return Page(null);
        });
        backend.OnSearch(West, Observation, Page(null));

        Task<RevIncludePage> execution = Executor(backend.Client, registry, 2)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 2, caller.Token);
        await backend.Client.WhenStarted(1).WaitAsync(GateTimeout, Token);
        execution.IsCompleted.ShouldBeFalse();
        release.SetResult();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        backend.Client.Calls.Select(call => call.Shard).ShouldBe([East]);
        backend.Client.InFlight.ShouldBe(0);
    }

    [Fact]
    public async Task CallerCancellationDuringANextWins()
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, East, Observation, "2");
        backend.OnSearch(East, Observation, Page(second));
        backend.Client.OnContinue(second, new TaskCompletionSource(), Page(null));
        backend.OnSearch(West, Observation, Page(null));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<RevIncludePage> execution = Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 3, caller.Token);
        await backend.Client.WhenStarted(2).WaitAsync(GateTimeout, Token);
        await caller.CancelAsync();

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        backend.Client.InFlight.ShouldBe(0);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("invalid")]
    [InlineData("last")]
    [InlineData("another")]
    public async Task CallerCancellationWinsWhenANeededFollowIgnoresItsToken(string outcome)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        ShardCursor second = Cursor(registry, West, Observation, "2");
        ShardCursor third = Cursor(registry, West, Observation, "3");
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        backend.OnSearch(East, Observation, Page(null));
        backend.OnSearch(West, Observation, Page(second));
        backend.OnContinue(second, async _ =>
        {
            await caller.CancelAsync();
            return outcome switch
            {
                "failed" => throw new InvalidOperationException("backend failed"),
                "invalid" => Page(second),
                "another" => Page(third, Entry(West, Observation, "o1")),
                _ => Page(null, Entry(West, Observation, "o1")),
            };
        });
        backend.OnContinue(third, _ => Task.FromResult(Page(null)));

        Task<RevIncludePage> execution = Executor(backend.Client, registry)
            .ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 4, caller.Token);

        OperationCanceledException thrown = await Should.ThrowAsync<OperationCanceledException>(execution);
        thrown.CancellationToken.ShouldBe(caller.Token);
        backend.Client.Calls.Count.ShouldBe(3);
        backend.Client.InFlight.ShouldBe(0);
    }

    // A live caller sees the shard's own failure: a backend timeout is not mistaken for the caller's cancellation.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ALiveCallerGetsTheShardsFailure(bool onNext, bool timesOut)
    {
        ShardRegistry registry = Registry();
        var backend = new RevIncludeBackend();
        Exception failure = timesOut ? new TaskCanceledException("backend call timed out") : new InvalidOperationException("backend failed");
        ShardCursor second = Cursor(registry, East, Observation, "2");
        if (onNext)
        {
            backend.OnSearch(East, Observation, Page(second));
            backend.OnContinue(second, _ => Task.FromException<ShardPage>(failure));
        }
        else
        {
            backend.OnSearchThrows(East, Observation, failure);
        }

        backend.OnSearch(West, Observation, Page(null));

        ShardFailureException thrown = await Should.ThrowAsync<ShardFailureException>(
            Executor(backend.Client, registry).ExecuteAsync(Plan(registry, Observations), [Match(East, "a")], ReadAll, 10, 3, Token));

        thrown.Shard.ShouldBe(East);
        thrown.Reason.ShouldBe(timesOut ? ShardFailureReason.Timeout : ShardFailureReason.Failed);
        thrown.InnerException.ShouldBeOfType(failure.GetType());
    }

    private static ShardPage Outcome(ShardId shard, string outcome) => outcome switch
    {
        "valid" => Page(null, Entry(shard, Observation, "o1")),
        "invalid" => Page(null, Entry(shard, Encounter, "e1")),
        _ => throw new InvalidOperationException("backend failed"),
    };
}

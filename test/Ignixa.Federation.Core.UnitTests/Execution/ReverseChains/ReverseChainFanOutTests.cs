using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.ReverseChains;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Execution.Counts;
using Ignixa.Federation.Core.UnitTests.Execution.Includes;
using Ignixa.Federation.Core.UnitTests.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Execution.Includes.IncludeFixture;

namespace Ignixa.Federation.Core.UnitTests.Execution.ReverseChains;

public sealed class ReverseChainFanOutTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SubqueryDeadlineFailsEvenIfAClientReturnsALateSuccess(bool honorsCancellation)
    {
        var fixture = new ReverseChainFixture();
        fixture.Client.OnSearch(East, async (_, token) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException) when (!honorsCancellation)
            {
                // Simulate a backend that completed successfully as the deadline expired.
            }

            return Page(null);
        });
        fixture.Client.OnSearch(West, Page(null));
        var resolving = fixture.Resolver(timeout: TimeSpan.FromMilliseconds(100))
            .ResolveAsync(fixture.Plan(), AllTypes, 2, TestContext.Current.CancellationToken);
        var exception = await Should.ThrowAsync<ShardFailureException>(resolving);
        exception.Reason.ShouldBe(ShardFailureReason.Timeout);
        exception.Shard.ShouldBe(East);
        fixture.Client.InFlight.ShouldBe(0);
    }

    [Theory]
    [InlineData("client fault")]
    [InlineData("client cancellation")]
    [InlineData("shard failure")]
    [InlineData("rows")]
    [InlineData("keys")]
    [InlineData("budget")]
    [InlineData("caller cancels during join")]
    public async Task FirstFailureStopsAndJoinsTheSiblingBeforeSurfacing(string failure)
    {
        var fixture = new ReverseChainFixture();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.OnSearch(East, async (_, token) =>
        {
            using var registration = token.Register(() => stopped.TrySetResult());
            await finishCleanup.Task;
            token.ThrowIfCancellationRequested();
            return Page(null);
        });
        var original = new ShardFailureException(West, ShardFailureReason.ContractViolation, null);
        fixture.Client.OnSearch(West, (request, _) => Task.FromResult(failure switch
        {
            "rows" or "keys" => Page(null, Obs(West, "o1", "Patient/a"), Obs(West, "o2", "Patient/b")),
            "budget" => Page(Cursor(request, TestShards.MicrosoftFhirServer, "over-budget")),
            "shard failure" => throw original,
            "client cancellation" => throw new OperationCanceledException(),
            _ => throw new InvalidOperationException("backend fault"),
        }));
        var resolving = fixture.Resolver(keys: failure == "keys" ? 1 : 10, rows: failure == "rows" ? 1 : 10)
            .ResolveAsync(fixture.Plan(), AllTypes, 2, caller.Token);
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            resolving.IsCompleted.ShouldBeFalse();
            if (failure == "caller cancels during join")
            {
                await caller.CancelAsync();
            }
        }
        finally
        {
            finishCleanup.TrySetResult();
            await ((Task)resolving).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        fixture.Client.InFlight.ShouldBe(0);
        fixture.Client.Finished.Count.ShouldBe(2);
        switch (failure)
        {
            case "rows":
            case "keys":
                (await Should.ThrowAsync<ReverseChainRefusedException>(resolving)).Reason.ShouldBe(ReverseChainRefusal.TooCostly);
                break;
            case "budget":
                await Should.ThrowAsync<FetchBudgetExhaustedException>(resolving);
                break;
            case "caller cancels during join":
                (await Should.ThrowAsync<OperationCanceledException>(resolving)).CancellationToken.ShouldBe(caller.Token);
                break;
            case "shard failure":
                (await Should.ThrowAsync<ShardFailureException>(resolving)).ShouldBeSameAs(original);
                break;
            default:
                var exception = await Should.ThrowAsync<ShardFailureException>(resolving);
                exception.Shard.ShouldBe(West);
                exception.Reason.ShouldBe(failure == "client fault" ? ShardFailureReason.Failed : ShardFailureReason.Timeout);
                break;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ParallelSourceCallsFillButDoNotExceedTheCap(int cap)
    {
        var fixture = new ReverseChainFixture
        {
            Capabilities = CountFixture.Certified(TestShards.Create(TestShards.East(), TestShards.West(),
                TestShards.Shard("north", "n4rt", "https://north.example/fhir"))),
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var shard in fixture.Capabilities.Registry.Shards)
        {
            fixture.Client.OnSearch(shard.Id, async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return Page(null);
            });
        }

        var token = TestContext.Current.CancellationToken;
        var resolving = fixture.Resolver(parallel: cap).ResolveAsync(fixture.Plan(), AllTypes, 3, token);
        try
        {
            await fixture.Client.WhenStarted(cap).WaitAsync(TimeSpan.FromSeconds(3), token);
            fixture.Client.InFlight.ShouldBe(cap);
        }
        finally
        {
            release.TrySetResult();
            await resolving;
        }

        fixture.Client.MaxInFlight.ShouldBe(cap);
        fixture.Client.Finished.Count.ShouldBe(3);
    }
}

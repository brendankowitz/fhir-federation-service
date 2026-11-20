using System.Diagnostics.Metrics;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Diagnostics;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Budgets;

/// <summary>
/// Exercises <see cref="RequestBudget"/> with the real <see cref="LimitOptions"/>, <see cref="TimeoutOptions"/> and
/// <see cref="CancellationTokenSource"/>, and a manual <see cref="TimeProvider"/> for the deadline (design spec §7.8, §8).
/// </summary>
public sealed class RequestBudgetTests
{
    private const int ParallelWorkers = 8;

    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(30);

    private static RequestBudget Create(
        int callsPerShard = 3,
        int shardCount = 2,
        long maxBytes = 100,
        TimeSpan? request = null,
        TimeProvider? time = null,
        CancellationToken? caller = null) =>
        new(
            new LimitOptions { BackendCallsPerShard = callsPerShard, MaxResponseBytes = maxBytes },
            new TimeoutOptions { Request = request ?? RequestDeadline },
            shardCount,
            time ?? new ManualTimeProvider(),
            caller ?? CancellationToken.None);

    // Runs the action on dedicated threads released together, so the budget really is contended.
    private static void RunContended(int workers, Action<int> action)
    {
        using var start = new ManualResetEventSlim();
        Task[] tasks = [.. Enumerable.Range(0, workers).Select(i => Task.Factory.StartNew(
            () =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                action(i);
            },
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default))];
        start.Set();
        Task.WaitAll(tasks, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BackendCalls_UpToTheExactGlobalAllowance_AreAllowed()
    {
        using RequestBudget budget = Create(callsPerShard: 3, shardCount: 2);

        for (int i = 0; i < 6; i++)
        {
            budget.BeforeBackendCall();
        }

        budget.BackendCalls.ShouldBe(6);
        Should.NotThrow(budget.Checkpoint);
    }

    [Fact]
    public void BackendCalls_OneBeyondTheAllowance_FailsTooCostly_AndStaysFailed()
    {
        using RequestBudget budget = Create(callsPerShard: 3, shardCount: 2);
        for (int i = 0; i < 6; i++)
        {
            budget.BeforeBackendCall();
        }

        RequestTooCostlyException first = Should.Throw<RequestTooCostlyException>(budget.BeforeBackendCall);
        first.Resource.ShouldBe(BudgetResource.BackendCalls);
        first.Limit.ShouldBe(6);

        // No refund: every later attempt is refused too, refusals are not reservations, and the final checkpoint cannot pass.
        Should.Throw<RequestTooCostlyException>(budget.BeforeBackendCall);
        budget.BackendCalls.ShouldBe(6);
        Should.Throw<RequestTooCostlyException>(budget.Checkpoint).Resource.ShouldBe(BudgetResource.BackendCalls);
    }

    [Fact]
    public void BackendCalls_AfterAByteBreach_AreRefusedWithoutAReservation()
    {
        using RequestBudget budget = Create(maxBytes: 100);
        budget.BeforeBackendCall();
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(101));

        Should.Throw<RequestTooCostlyException>(budget.BeforeBackendCall).Resource.ShouldBe(BudgetResource.ResponseBytes);

        budget.BackendCalls.ShouldBe(1);
    }

    [Fact]
    public void BackendCalls_AreOneGlobalAllowance_NotABucketPerShard()
    {
        // 1 call per shard over 4 shards: one stage may use all four calls.
        using RequestBudget budget = Create(callsPerShard: 1, shardCount: 4);

        for (int i = 0; i < 4; i++)
        {
            budget.BeforeBackendCall();
        }

        Should.Throw<RequestTooCostlyException>(budget.BeforeBackendCall);
    }

    [Fact]
    public void BackendCalls_UnderContention_AllowExactlyTheAllowance()
    {
        const int Allowance = 1000;
        using RequestBudget budget = Create(callsPerShard: 250, shardCount: 4);
        int allowed = 0;
        int refused = 0;

        RunContended(ParallelWorkers, _ =>
        {
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    budget.BeforeBackendCall();
                    Interlocked.Increment(ref allowed);
                }
                catch (RequestTooCostlyException)
                {
                    Interlocked.Increment(ref refused);
                }
            }
        });

        allowed.ShouldBe(Allowance);
        refused.ShouldBe((ParallelWorkers * 500) - Allowance);
    }

    [Fact]
    public void ResponseBytes_UpToTheExactLimit_Complete()
    {
        using RequestBudget budget = Create(maxBytes: 100);

        budget.OnResponseBytesConsumed(60);
        budget.OnResponseBytesConsumed(40);

        budget.ResponseBytes.ShouldBe(100);
        Should.NotThrow(budget.Checkpoint);
    }

    [Fact]
    public void ResponseBytes_CrossingChunk_IsChargedInFull_ThenFails()
    {
        using RequestBudget budget = Create(maxBytes: 100);
        budget.OnResponseBytesConsumed(60);

        RequestTooCostlyException ex = Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(100));

        ex.Resource.ShouldBe(BudgetResource.ResponseBytes);
        ex.Limit.ShouldBe(100);
        budget.ResponseBytes.ShouldBe(160);
    }

    [Fact]
    public void ResponseBytes_BreachCannotSucceedAtTheFinalCheckpoint_EvenWhenTheFailureWasSwallowed()
    {
        using RequestBudget budget = Create(maxBytes: 100);
        try
        {
            budget.OnResponseBytesConsumed(101);
        }
        catch (RequestTooCostlyException)
        {
            // A caller that swallows the callback failure must still not complete successfully.
        }

        Should.Throw<RequestTooCostlyException>(budget.Checkpoint).Resource.ShouldBe(BudgetResource.ResponseBytes);
    }

    [Fact]
    public void ResponseBytes_WhenTheLimitIsLongMaxValue_TheExactLimitCompletes_AndTheNextByteBreaches()
    {
        using RequestBudget budget = Create(maxBytes: long.MaxValue);

        budget.OnResponseBytesConsumed(long.MaxValue - 1);
        budget.OnResponseBytesConsumed(1);
        budget.ResponseBytes.ShouldBe(long.MaxValue);
        Should.NotThrow(budget.Checkpoint);

        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(1)).Resource.ShouldBe(BudgetResource.ResponseBytes);
        budget.ResponseBytes.ShouldBe(long.MaxValue);
        Should.Throw<RequestTooCostlyException>(budget.Checkpoint);
    }

    [Fact]
    public void ResponseBytes_ArithmeticDoesNotWrap_WhenAHugeChunkBreachesASmallLimit()
    {
        using RequestBudget budget = Create(maxBytes: 100);
        budget.OnResponseBytesConsumed(50);

        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(long.MaxValue));
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(long.MaxValue));

        budget.ResponseBytes.ShouldBe(long.MaxValue);
    }

    [Fact]
    public void ResponseBytes_ZeroByteChunk_IsAllowed_AndNegativeIsRejected()
    {
        using RequestBudget budget = Create();

        budget.OnResponseBytesConsumed(0);

        Should.Throw<ArgumentOutOfRangeException>(() => budget.OnResponseBytesConsumed(-1));
        budget.ResponseBytes.ShouldBe(0);
    }

    [Fact]
    public void ResponseBytes_UnderContention_ChargeEveryByte_AndFailOnlyBeyondTheLimit()
    {
        const int Limit = 10_000;
        using RequestBudget budget = Create(maxBytes: Limit);
        int failures = 0;

        RunContended(ParallelWorkers, _ =>
        {
            for (int i = 0; i < 2500; i++)
            {
                try
                {
                    budget.OnResponseBytesConsumed(1);
                }
                catch (RequestTooCostlyException)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });

        budget.ResponseBytes.ShouldBe(20_000);
        failures.ShouldBe(10_000);
    }

    [Fact]
    public void ResponseBytes_ExactlyAtTheLimit_UnderContention_NeverFail()
    {
        const int Limit = 8 * 1000;
        using RequestBudget budget = Create(maxBytes: Limit);

        RunContended(ParallelWorkers, _ =>
        {
            for (int i = 0; i < 1000; i++)
            {
                budget.OnResponseBytesConsumed(1);
            }
        });

        budget.ResponseBytes.ShouldBe(Limit);
        Should.NotThrow(budget.Checkpoint);
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(1));
    }

    [Fact]
    public void Breach_DoesNotCancelTheSharedToken_AndAnInFlightChunkIsStillCharged()
    {
        using RequestBudget budget = Create(callsPerShard: 1, shardCount: 1, maxBytes: 100);
        budget.BeforeBackendCall();
        Should.Throw<RequestTooCostlyException>(budget.BeforeBackendCall);

        budget.Token.IsCancellationRequested.ShouldBeFalse();
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(70));

        budget.ResponseBytes.ShouldBe(70);
    }

    [Fact]
    public void Budgets_AreIndependentPerRequest()
    {
        using RequestBudget first = Create(callsPerShard: 1, shardCount: 1);
        using RequestBudget second = Create(callsPerShard: 1, shardCount: 1);
        first.BeforeBackendCall();
        Should.Throw<RequestTooCostlyException>(first.BeforeBackendCall);

        second.BeforeBackendCall();
        second.OnResponseBytesConsumed(100);

        first.ResponseBytes.ShouldBe(0);
        second.BackendCalls.ShouldBe(1);
        second.Token.IsCancellationRequested.ShouldBeFalse();
        Should.NotThrow(second.Checkpoint);
    }

    [Fact]
    public void Deadline_Passing_CancelsTheToken_AndIsClassifiedAsRequestExpiry()
    {
        var time = new ManualTimeProvider();
        using RequestBudget budget = Create(time: time);

        time.Advance(RequestDeadline - TimeSpan.FromMilliseconds(1));
        budget.Token.IsCancellationRequested.ShouldBeFalse();
        Should.NotThrow(budget.Checkpoint);

        time.Advance(TimeSpan.FromMilliseconds(1));

        budget.Token.IsCancellationRequested.ShouldBeTrue();
        RequestDeadlineExceededException ex = Should.Throw<RequestDeadlineExceededException>(budget.Checkpoint);
        ex.Deadline.ShouldBe(RequestDeadline);
    }

    [Fact]
    public void Deadline_ContainsThrowingRegistrationsAndLeavesFaultDeliveryToCollection()
    {
        var time = new ManualTimeProvider();
        using RequestBudget budget = Create(time: time);
        using var listener = new MeterListener();
        var measurements = new List<(long Count, int TagCount)>();
        var listenerFault = new InvalidOperationException("Synthetic measurement listener fault.");
        bool throwFromListener = false;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (ReferenceEquals(instrument.Meter, FederationTelemetry.Meter)
                && instrument.Name == "federation.request.deadline_callback_faults")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, count, tags, _) =>
        {
            measurements.Add((count, tags.Length));
            if (throwFromListener)
            {
                throw listenerFault;
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        long baseline = measurements.Count == 0 ? 0 : measurements.Single().Count;
        measurements.Clear();

        bool peerNotified = false;
        using CancellationTokenRegistration peer = budget.Token.Register(() => peerNotified = true);
        using CancellationTokenRegistration faulty = budget.Token.Register(
            () => throw new InvalidOperationException("Synthetic callback detail."));

        throwFromListener = true;
        Should.NotThrow(() => time.Advance(RequestDeadline));

        budget.Token.IsCancellationRequested.ShouldBeTrue();
        peerNotified.ShouldBeTrue();
        Should.Throw<RequestDeadlineExceededException>(budget.Checkpoint);
        measurements.ShouldBeEmpty();

        Should.Throw<AggregateException>(listener.RecordObservableInstruments)
            .InnerExceptions.ShouldBe([listenerFault]);
        measurements.ShouldBe([(baseline + 1, 0)]);
    }

    [Fact]
    public void Deadline_IsCheckedAtDecisionPoints_WithoutADeliveredTimerCallback()
    {
        var time = new ManualTimeProvider { TimersEnabled = false };
        using RequestBudget budget = Create(time: time);

        time.Advance(RequestDeadline);

        budget.Token.IsCancellationRequested.ShouldBeFalse();
        Should.Throw<RequestDeadlineExceededException>(budget.BeforeBackendCall);
        budget.BackendCalls.ShouldBe(0);
        Should.Throw<RequestDeadlineExceededException>(() => budget.OnResponseBytesConsumed(10));
        budget.ResponseBytes.ShouldBe(10);
    }

    [Fact]
    public void Deadline_BeyondThePlatformTimerRange_DoesNotExpireEarly_AndCancelsTheTokenAtTheExactDeadline()
    {
        var time = new ManualTimeProvider();
        TimeSpan sixtyDays = TimeSpan.FromDays(60);
        using RequestBudget budget = Create(request: sixtyDays, time: time);

        time.Advance(TimeSpan.FromDays(50));
        budget.Token.IsCancellationRequested.ShouldBeFalse();
        Should.NotThrow(budget.Checkpoint);
        budget.BeforeBackendCall();

        time.Advance(sixtyDays - TimeSpan.FromDays(50) - TimeSpan.FromMilliseconds(1));
        budget.Token.IsCancellationRequested.ShouldBeFalse();
        Should.NotThrow(budget.Checkpoint);

        time.Advance(TimeSpan.FromMilliseconds(1));
        budget.Token.IsCancellationRequested.ShouldBeTrue();
        Should.Throw<RequestDeadlineExceededException>(budget.BeforeBackendCall);
    }

    [Fact]
    public async Task Dispose_WhileTheDeadlineCallbackIsCancelling_DoesNotBlockOrThrow()
    {
        var time = new ManualTimeProvider();
        using RequestBudget budget = Create(time: time);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        TimeSpan waitLimit = TimeSpan.FromSeconds(10);
        CancellationToken testCancellation = TestContext.Current.CancellationToken;
        bool releasedBeforeTimeout = false;
        using CancellationTokenRegistration registration = budget.Token.Register(() =>
        {
            entered.SetResult();
            releasedBeforeTimeout = release.Wait(waitLimit, testCancellation);
        });

        Task advancing = Task.Run(() => time.Advance(RequestDeadline), testCancellation);
        try
        {
            await entered.Task.WaitAsync(waitLimit, testCancellation);
            budget.Dispose();
        }
        finally
        {
            release.Set();
        }

        await advancing.WaitAsync(waitLimit, testCancellation);
        releasedBeforeTimeout.ShouldBeTrue();
    }

    [Fact]
    public void CallerCancellation_IsClassifiedAsTheCallersToken_NotAsExpiryOrCost()
    {
        using var caller = new CancellationTokenSource();
        using RequestBudget budget = Create(caller: caller.Token);

        caller.Cancel();

        budget.Token.IsCancellationRequested.ShouldBeTrue();
        OperationCanceledException ex = Should.Throw<OperationCanceledException>(budget.Checkpoint);
        ex.CancellationToken.ShouldBe(caller.Token);
        Should.Throw<OperationCanceledException>(budget.BeforeBackendCall);
        budget.BackendCalls.ShouldBe(0);
    }

    [Fact]
    public void InFlightCrossingChunk_AfterCallerCancellation_IsCharged_ButDoesNotMaskTheCancellation()
    {
        using var caller = new CancellationTokenSource();
        using RequestBudget budget = Create(caller: caller.Token, maxBytes: 100);
        caller.Cancel();

        Should.Throw<OperationCanceledException>(() => budget.OnResponseBytesConsumed(150))
            .CancellationToken.ShouldBe(caller.Token);

        budget.ResponseBytes.ShouldBe(150);
        Should.Throw<OperationCanceledException>(budget.Checkpoint);
    }

    [Fact]
    public void InFlightCrossingChunk_AfterExpiry_IsCharged_ButDoesNotMaskTheDeadline()
    {
        var time = new ManualTimeProvider { TimersEnabled = false };
        using RequestBudget budget = Create(time: time, maxBytes: 100);
        time.Advance(RequestDeadline);

        Should.Throw<RequestDeadlineExceededException>(() => budget.OnResponseBytesConsumed(150));

        budget.ResponseBytes.ShouldBe(150);
        Should.Throw<RequestDeadlineExceededException>(budget.Checkpoint);
    }

    [Fact]
    public void InFlightCrossingChunk_AfterBothCallerCancellationAndExpiry_PrefersTheCaller()
    {
        var time = new ManualTimeProvider();
        using var caller = new CancellationTokenSource();
        using RequestBudget budget = Create(time: time, caller: caller.Token, maxBytes: 100);
        caller.Cancel();
        time.Advance(RequestDeadline);

        Should.Throw<OperationCanceledException>(() => budget.OnResponseBytesConsumed(150))
            .CancellationToken.ShouldBe(caller.Token);
    }

    [Fact]
    public void CostBreach_WinsOverALaterCallerCancellation()
    {
        using var caller = new CancellationTokenSource();
        using RequestBudget budget = Create(caller: caller.Token, maxBytes: 10);
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(11));

        caller.Cancel();

        Should.Throw<RequestTooCostlyException>(budget.Checkpoint);
    }

    [Fact]
    public void AlreadyCancelledCaller_IsClassifiedAtTheFirstCheckpoint()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        using RequestBudget budget = Create(caller: caller.Token);

        Should.Throw<OperationCanceledException>(budget.BeforeBackendCall).CancellationToken.ShouldBe(caller.Token);
    }

    [Fact]
    public async Task UnrelatedCancellation_IsNotClassifiedAsTheRequestsOwn()
    {
        using RequestBudget budget = Create();
        using var stageLocal = new CancellationTokenSource();
        await stageLocal.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.Infinite, stageLocal.Token));

        // The checkpoint finds nothing of the request's own: the caller handles its local cancellation as it sees fit.
        Should.NotThrow(budget.Checkpoint);
        budget.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void CostBreach_WinsOverALaterDeadline()
    {
        var time = new ManualTimeProvider();
        using RequestBudget budget = Create(time: time, maxBytes: 10);
        Should.Throw<RequestTooCostlyException>(() => budget.OnResponseBytesConsumed(11));

        time.Advance(RequestDeadline);

        Should.Throw<RequestTooCostlyException>(budget.Checkpoint);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        RequestBudget budget = Create();

        budget.Dispose();

        Should.NotThrow(budget.Dispose);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShardCount_MustBePositive(int shardCount) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Create(shardCount: shardCount));
}

using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A credential provider whose synchronous portion stalls (a blocking file open, say) holds neither the caller past its
/// deadline or cancellation, nor more than a bounded number of threads, nor the shard's breaker (design spec §7.8, §8).
/// Deadlines move on a manual clock and the stall ends only when a test releases it, so every oracle is an ordering the
/// test controls; the wall-clock bounds only keep a regression from hanging the run.
/// </summary>
public sealed class HttpShardClientCredentialStallTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AProviderStalledBeforeItsFirstAwait_DoesNotHoldTheCallPastItsDeadline()
    {
        var deadline = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: CallTimeout, clock: new ManualTimeProvider(), deadlineClock: deadline);
        using var stall = new SynchronousStall(shard.Credentials);

        Task<ShardPage> call = OffTheTestThread(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct));
        await stall.EnteredAsync();
        deadline.Advance(CallTimeout);

        ShardCallException failure = await Should.ThrowAsync<ShardCallException>(() => Guard(call));

        failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        stall.Returned.ShouldBe(0, "the call must end while the provider is still stalled");
        shard.Meter.Sends.ShouldBeEmpty();
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AProviderStalledBeforeItsFirstAwait_DoesNotHoldTheCallPastCallerCancellation()
    {
        await using var shard = ShardHarness.Start(callTimeout: CallTimeout, clock: new ManualTimeProvider(), deadlineClock: new ManualTimeProvider());
        using var stall = new SynchronousStall(shard.Credentials);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<ShardPage> call = OffTheTestThread(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), caller.Token));
        await stall.EnteredAsync();
        await caller.CancelAsync();

        OperationCanceledException canceled = await Should.ThrowAsync<OperationCanceledException>(() => Guard(call));

        canceled.CancellationToken.ShouldBe(caller.Token);
        stall.Returned.ShouldBe(0, "the call must end while the provider is still stalled");
        shard.Server.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledAcquisitions_AreBounded_AndFinishingOrFaultingFreesASlot(bool lateFault)
    {
        var deadline = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: CallTimeout, clock: new ManualTimeProvider(), deadlineClock: deadline);
        using var stall = new SynchronousStall(shard.Credentials)
        {
            Fault = lateFault ? new InvalidOperationException("synthetic late provider fault") : null,
        };
        const int bound = HttpShardClient.MaxPendingAcquisitions;

        // Fill every slot with a stalled provider call, then abandon each one at its deadline.
        var held = new List<Task<ShardPage>>();
        for (int i = 0; i < bound; i++)
        {
            held.Add(OffTheTestThread(() => shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)));
            await stall.EnteredAsync();
        }

        deadline.Advance(CallTimeout);
        foreach (Task<ShardPage> abandoned in held)
        {
            (await Should.ThrowAsync<ShardCallException>(() => Guard(abandoned))).Failure.ShouldBe(ShardCallFailure.Timeout);
        }

        stall.Returned.ShouldBe(0, "every slot is still held by an abandoned provider call");

        // While they stay stalled, later calls wait for a slot within their own deadline and never reach the provider. More
        // of them than the breaker's throughput would open the circuit if a credential timeout counted against the backend.
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            Task<ShardPage> waiting = shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct);
            deadline.Advance(CallTimeout);
            (await Should.ThrowAsync<ShardCallException>(() => Guard(waiting))).Failure.ShouldBe(ShardCallFailure.Timeout);
        }

        shard.Credentials.Calls.ShouldBe(bound, "an abandoned waiter must leave no queued provider call behind");

        // One stalled call finishing frees exactly its slot; the next call waits for it, reuses it and reaches the backend.
        shard.Credentials.Acquire = _ => ValueTask.FromResult(SyntheticCredentials.Token);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        stall.ReleaseOne();
        await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        shard.Credentials.Calls.ShouldBe(bound + 1);
        shard.Server.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ATokenArrivingAsTheDeadlineFires_IsATimeoutThatTheBreakerDoesNotCount()
    {
        var deadline = new ManualTimeProvider();
        await using var shard = ShardHarness.Start(callTimeout: CallTimeout, clock: new ManualTimeProvider(), deadlineClock: deadline);

        // The provider completes synchronously, but only once the call's deadline has passed.
        shard.Credentials.Acquire = _ =>
        {
            deadline.Advance(CallTimeout);
            return ValueTask.FromResult(SyntheticCredentials.Token);
        };
        for (int i = 0; i < ShardResilience.MinimumThroughput; i++)
        {
            ShardCallException failure = await Should.ThrowAsync<ShardCallException>(
                () => Guard(shard.Client.SearchAsync(shard.SarahRequest(ShardSearchMethod.PostSearch), Ct)));
            failure.Failure.ShouldBe(ShardCallFailure.Timeout);
        }

        shard.Credentials.Acquire = _ => ValueTask.FromResult(SyntheticCredentials.Token);
        shard.Server.Respond(ScriptedResponse.Json(shard.SarahPage()));
        await Guard(shard.Client.SearchAsync(shard.SarahRequest(), Ct));

        shard.Server.Requests.ShouldHaveSingleItem();
    }

    // Starts a call on a pool thread, so a client that runs the stalled provider on its caller's thread blocks that thread
    // rather than the test.
    private static Task<T> OffTheTestThread<T>(Func<Task<T>> call) => Task.Run(call, Ct);

    // Longer than a stall's own safety bound, so a client that waits for the provider fails an ordering assertion first.
    private static Task<T> Guard<T>(Task<T> call) => call.WaitAsync(SynchronousStall.SafetyBound * 2, Ct);

    /// <summary>
    /// A provider that blocks its calling thread before returning, as a synchronous file open or a blocking lookup would,
    /// until the test releases it. It ignores cancellation. A safety bound returns it eventually, so a failing test does
    /// not pin a pool thread for good. Disposing releases every call still stalled.
    /// </summary>
    [SuppressMessage("Reliability", "CA2213:Disposable fields should be disposed",
        Justification = "A released provider thread may still be using the semaphores after the test ends; they hold no wait handle.")]
    private sealed class SynchronousStall : IDisposable
    {
        public static readonly TimeSpan SafetyBound = TimeSpan.FromSeconds(20);

        private readonly SemaphoreSlim _entered = new(0);
        private readonly SemaphoreSlim _release = new(0);
        private int _returned;

        public SynchronousStall(SyntheticCredentials credentials) => credentials.Acquire = Acquire;

        /// <summary>Gets the exception a released call throws instead of returning a token.</summary>
        public Exception? Fault { get; init; }

        /// <summary>Gets how many stalled calls have returned or thrown.</summary>
        public int Returned => Volatile.Read(ref _returned);

        /// <summary>Waits until one more call has entered the provider and stalled.</summary>
        public async Task EnteredAsync() => (await _entered.WaitAsync(SafetyBound, Ct)).ShouldBeTrue("the provider was never called");

        public void ReleaseOne() => _release.Release();

        public void Dispose() => _release.Release(64);

        [SuppressMessage("Performance", "CA1849:Call async methods when in an async method",
            Justification = "Blocking the calling thread before the first await is the behaviour under test.")]
        private ValueTask<string> Acquire(CancellationToken _)
        {
            _entered.Release();
            _release.Wait(SafetyBound, CancellationToken.None);
            Exception? fault = Fault;
            Interlocked.Increment(ref _returned);
            return fault is null ? ValueTask.FromResult(SyntheticCredentials.Token) : throw fault;
        }
    }
}

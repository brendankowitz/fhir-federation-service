using Ignixa.Federation.Core.Shards;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// One shard's transport resilience (design spec §8): at most one retry of a GET, and a circuit breaker over every search,
/// continuation and read. It holds the breaker's state, so every <see cref="HttpShardClient"/> that reaches the same shard
/// should share one instance; a client given none creates its own.
/// </summary>
/// <remarks>
/// <para><b>What counts.</b> The breaker counts a call as a failure only when <see cref="HttpShardClient"/> saw the backend
/// or the wire fail: a transient status (408, 429 or 5xx), a connection that was refused, reset or closed early, or a call
/// that outlasted its deadline. Any other answer, including 400, 401, 403, 404, a malformed or oversized body, counts as the
/// backend being alive. Cancellation and the credential provider's or meter's own exceptions (including a
/// <see cref="ShardCallException"/> or <see cref="BrokenCircuitException"/> they throw, which keep their identity) say
/// nothing about the backend. In a closed circuit they are recorded as successes: they are not failures, but they do dilute
/// the failure ratio. A probe that ends that way proved nothing, so it reopens the circuit and never closes it.</para>
/// <para><b>Retry.</b> Only a transient status or wire fault on a GET is retried, once, after a short delay and within the
/// call's single deadline. A timeout is never retried. A POST is never retried, however it failed. Each attempt, including a
/// retry, is its own send to the transfer meter, which may refuse it; a refusal propagates unchanged and nothing is sent.</para>
/// <para><b>Breaker.</b> With at least five attempts in the 30-second sampling window and a failure ratio of at least
/// one half, the circuit opens and calls fail at once with <see cref="ShardCallFailure.CircuitOpen"/>. The first attempt
/// after 15 seconds is the probe; until it ends every other attempt still fails at once. Only the probe decides recovery:
/// an answer closes the circuit with an empty sampling window, anything else opens it for another 15 seconds. A retry
/// counts as an attempt. The credential is acquired inside the attempt, so an open circuit rejects a call before any
/// credential is requested. A retry that meets the circuit its first attempt opened fails with
/// <see cref="ShardCallFailure.CircuitOpen"/> although that first attempt was sent. Metadata refresh bypasses all of this.</para>
/// <para><b>Generations.</b> Every attempt belongs to the circuit generation that admitted it, and a generation ends when
/// the circuit opens or a probe closes it. An attempt that ends after its generation did, such as one admitted before the
/// circuit opened and answered while a probe is in flight, is not counted anywhere: it neither decides the probe nor
/// enters the next closed window. Polly's breaker only samples the closed window; this type governs admission and
/// recovery, because Polly classifies an outcome by the circuit's state when it ends, not when it was admitted.</para>
/// <para>Thread-safe. Network, credential, and metering work runs outside the circuit lock. Nothing needs disposing.</para>
/// </remarks>
public sealed class ShardResilience
{
    internal const int MinimumThroughput = 5;

    internal static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private const double FailureRatio = 0.5;

    private readonly ShardDescriptor _shard;
    private readonly TimeProvider _clock;
    private readonly ResiliencePipeline _retry;

    // Guards every field below; held only for bookkeeping, never across an attempt.
    private readonly Lock _gate = new();
    private Phase _phase = Phase.Closed;

    // Advances whenever the circuit opens or a probe closes it, so an attempt's outcome can be matched to the generation
    // that admitted it.
    private long _generation;
    private DateTimeOffset _openUntil;
    private ClosedWindow _window;

    /// <summary>Creates the resilience state for one shard.</summary>
    /// <param name="shard">The registered descriptor. Share this state only with clients using that same descriptor.</param>
    /// <param name="timeProvider">The clock for the break period and retry delay, or <see langword="null"/> for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public ShardResilience(ShardDescriptor shard, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(shard);
        _shard = shard;
        _clock = timeProvider ?? TimeProvider.System;
        _window = new ClosedWindow(_clock);
        _retry = new ResiliencePipelineBuilder { TimeProvider = _clock }
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = RetryDelay,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is ShardCallException { Fault: ShardFault.Transient }),
            })
            .Build();
    }

    private enum Phase
    {
        Closed,
        Open,
        HalfOpen,
    }

    /// <summary>Gets the shard this state belongs to.</summary>
    public ShardId Shard => _shard.Id;

    internal bool IsFor(ShardDescriptor shard) => ReferenceEquals(_shard, shard);

    internal async Task<T> ExecuteAsync<T>(bool retry, Func<CancellationToken, ValueTask<T>> attempt, CancellationToken cancellationToken) =>
        retry
            ? await _retry.ExecuteAsync(token => AttemptAsync(attempt, token), cancellationToken).ConfigureAwait(false)
            : await AttemptAsync(attempt, cancellationToken).ConfigureAwait(false);

    // Only an answer, even an unwelcome one, proves the backend alive. A probe that ended any other way proved nothing.
    private static bool Answered(Exception? exception) =>
        exception is null or ShardCallException { Fault: ShardFault.Answered };

    // Only what this client saw the backend or the wire do is a failure. Outcomes that say nothing about the backend
    // (cancellation, a credential provider's or meter's own exception, a ShardCallException this client did not make)
    // are recorded as successes, because the window has no third option.
    private static bool CountsAgainstBackend(Exception? exception) =>
        exception is ShardCallException { Fault: ShardFault.Transient or ShardFault.TimedOut };

    // One attempt through the circuit, a retry included: admitted or rejected, and if admitted its outcome is reported to
    // the generation that admitted it.
    private async ValueTask<T> AttemptAsync<T>(Func<CancellationToken, ValueTask<T>> attempt, CancellationToken cancellationToken)
    {
        if (Admit() is not { } generation)
        {
            // A cancellation seen by now outranks the rejection, however the two raced.
            cancellationToken.ThrowIfCancellationRequested();

            // An earlier attempt of this call may have been sent, so the message claims nothing about this one. A
            // BrokenCircuitException the attempt's own collaborators throw is theirs and passes through unchanged.
            throw new ShardCallException(_shard, ShardCallFailure.CircuitOpen, "The shard's circuit breaker is open; this attempt was rejected.");
        }

        T result;
        try
        {
            result = await attempt(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Report(generation, e);
            throw;
        }

        Report(generation, exception: null);
        return result;
    }

    // The generation after a trip admits only the probe, once the break period elapses.
    private long? Admit()
    {
        // A clock read early can only keep the circuit open longer.
        DateTimeOffset now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_phase == Phase.Open && now >= _openUntil)
            {
                _phase = Phase.HalfOpen;
                return _generation;
            }

            return _phase == Phase.Closed ? _generation : null;
        }
    }

    // An outcome counts only in the generation that admitted its attempt. A later generation began when the circuit opened
    // or a probe closed it, and an earlier attempt's outcome must neither decide that probe nor enter the new window.
    private void Report(long generation, Exception? exception)
    {
        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            // No attempt is admitted while open, so a current outcome is either the probe's or a closed window's.
            if (_phase == Phase.HalfOpen)
            {
                if (Answered(exception))
                {
                    _phase = Phase.Closed;
                    _window = new ClosedWindow(_clock);
                    _generation++;
                }
                else
                {
                    Open();
                }
            }
            else if (_window.RecordTrips(CountsAgainstBackend(exception)))
            {
                Open();
            }
        }
    }

    // Needs _gate. The window that tripped is retired with its generation; only a successful probe starts a new one.
    private void Open()
    {
        _openUntil = _clock.GetUtcNow() + BreakDuration;
        _phase = Phase.Open;
        _generation++;
    }

    // One closed generation's sampling window: Polly's breaker, fed only outcomes that have already ended, so it decides
    // when the circuit trips but never admits or rejects an attempt. It is retired once it trips, so it never reaches its
    // own half-open state. It has no callbacks or telemetry listener, so recording completes synchronously without I/O.
    private sealed class ClosedWindow
    {
        private readonly CircuitBreakerStateProvider _state = new();
        private readonly ResiliencePipeline<bool> _breaker;

        public ClosedWindow(TimeProvider clock)
        {
            _breaker = new ResiliencePipelineBuilder<bool> { TimeProvider = clock }
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<bool>
                {
                    FailureRatio = FailureRatio,
                    MinimumThroughput = MinimumThroughput,
                    SamplingDuration = SamplingDuration,
                    BreakDuration = BreakDuration,
                    StateProvider = _state,
                    ShouldHandle = static args => ValueTask.FromResult(args.Outcome.Result),
                })
                .Build();
        }

        // Records one ended attempt, true for a failure, and returns whether the window has now tripped. Never called
        // once it has, so the breaker never answers with its own rejection.
        public bool RecordTrips(bool failed)
        {
            _breaker.Execute(static outcome => outcome, failed);
            return _state.CircuitState != CircuitState.Closed;
        }
    }
}

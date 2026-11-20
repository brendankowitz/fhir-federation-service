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
/// nothing about the backend. Polly can only record a success or a failure, so in a closed circuit they are recorded as
/// successes: they are not failures, but they do dilute the failure ratio. While a probe is in flight they count as its
/// failure and reopen the circuit, because a probe that proved nothing must never close it.</para>
/// <para><b>Retry.</b> Only a transient status or wire fault on a GET is retried, once, after a short delay and within the
/// call's single deadline. A timeout is never retried. A POST is never retried, however it failed. Each attempt, including a
/// retry, is its own send to the transfer meter, which may refuse it; a refusal propagates unchanged and nothing is sent.</para>
/// <para><b>Breaker.</b> With at least five attempts in the 30-second sampling window and a failure ratio of at least
/// one half, the circuit opens and calls fail at once with <see cref="ShardCallFailure.CircuitOpen"/>. After 15 seconds
/// Polly admits a probe; a prior in-flight attempt can also affect the outcome if it completes during the half-open
/// state. A retry counts as an attempt. The credential is acquired inside the attempt, so an open circuit rejects a
/// call before any credential is requested. A retry that meets the circuit its first attempt opened fails with
/// <see cref="ShardCallFailure.CircuitOpen"/> although that first attempt was sent. Metadata refresh bypasses all of this.</para>
/// <para>Thread-safe. Creating one allocates only the strategy objects; nothing needs disposing.</para>
/// </remarks>
public sealed class ShardResilience
{
    internal const int MinimumThroughput = 5;

    internal static readonly TimeSpan SamplingDuration = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private const double FailureRatio = 0.5;

    private readonly ShardDescriptor _shard;
    private readonly CircuitBreakerStateProvider _state = new();
    private readonly ResiliencePipeline _breaker;
    private readonly ResiliencePipeline _retry;

    /// <summary>Creates the resilience state for one shard.</summary>
    /// <param name="shard">The registered descriptor. Share this state only with clients using that same descriptor.</param>
    /// <param name="timeProvider">The clock for the break period and retry delay, or <see langword="null"/> for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public ShardResilience(ShardDescriptor shard, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(shard);
        _shard = shard;
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        _breaker = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = FailureRatio,
                MinimumThroughput = MinimumThroughput,
                SamplingDuration = SamplingDuration,
                BreakDuration = BreakDuration,
                StateProvider = _state,
                ShouldHandle = args => ValueTask.FromResult(CountsAgainstBackend(args.Outcome.Exception)),
            })
            .Build();
        _retry = new ResiliencePipelineBuilder { TimeProvider = clock }
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

    /// <summary>Gets the shard this state belongs to.</summary>
    public ShardId Shard => _shard.Id;

    internal bool IsFor(ShardDescriptor shard) => ReferenceEquals(_shard, shard);

    // The breaker only has "handled" (a failure) and "not handled" (a success). Outcomes that say nothing about the backend
    // (cancellation, a credential provider's or meter's own exception, a ShardCallException this client did not make) are
    // recorded as successes, so they dilute the failure ratio in a closed circuit. While a probe is in flight they instead
    // count as its failure: a probe that proved nothing must never close the circuit.
    private bool CountsAgainstBackend(Exception? exception) => exception switch
    {
        null => false,
        ShardCallException { Fault: ShardFault.Transient or ShardFault.TimedOut } => true,
        ShardCallException { Fault: ShardFault.Answered } => false,
        _ => _state.CircuitState == CircuitState.HalfOpen,
    };

    // The circuit state is deliberately not read before a call: Polly only moves an open circuit to half open when a call
    // arrives, so a precheck would never let one through. Instead an open circuit throws, translated below.
    internal async Task<T> ExecuteAsync<T>(bool retry, Func<CancellationToken, ValueTask<T>> attempt, CancellationToken cancellationToken)
    {
        Exception? thrown = null;

        async ValueTask<T> Observe(CancellationToken token)
        {
            try
            {
                return await attempt(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                thrown = e;
                throw;
            }
        }

        try
        {
            return retry
                ? await _retry.ExecuteAsync(token => _breaker.ExecuteAsync(Observe, token), cancellationToken).ConfigureAwait(false)
                : await _breaker.ExecuteAsync(Observe, cancellationToken).ConfigureAwait(false);
        }
        catch (BrokenCircuitException e) when (!ReferenceEquals(e, thrown))
        {
            // A cancellation seen by now outranks the rejection, however the two raced.
            cancellationToken.ThrowIfCancellationRequested();

            // One the attempt itself threw (say from a meter) is that component's, and keeps its identity. An earlier
            // attempt of this call may have been sent, so the message claims nothing about this one.
            throw new ShardCallException(_shard, ShardCallFailure.CircuitOpen, "The shard's circuit breaker is open; this attempt was rejected.");
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Configuration;

namespace Ignixa.Federation.Api.Budgets;

/// <summary>
/// The live budget of one inbound federated operation (design spec §7.8, §8): a request-wide allowance of backend
/// call attempts and of consumed response-body bytes, and the overall request deadline. Create one per inbound
/// operation and share it across every shard and stage; the operation's owner disposes it after every callback has
/// finished.
/// </summary>
/// <remarks>
/// <para>
/// <b>Calls.</b> The allowance is <c>BackendCallsPerShard</c> times the shard count, one pool for the whole request
/// and not a bucket per shard. Call <see cref="BeforeBackendCall"/> before every actual attempt, including retries,
/// <c>next</c> follows and speculative work. A granted attempt is charged and never refunded; the attempt one beyond
/// the allowance, and any attempt once the request has stopped, throws without being charged and must not be sent.
/// </para>
/// <para>
/// <b>Bytes.</b> Call <see cref="OnResponseBytesConsumed"/> after each chunk of a response body has been consumed. The
/// chunk's full count is charged before any failure, so the total is exact even for the chunk that crosses the
/// limit and for a chunk still in flight after the request stopped. A total equal to the limit is allowed. The
/// crossing is decided against the remaining allowance, so it is exact for a limit of <see cref="long.MaxValue"/>
/// too; only the reported total saturates at <see cref="long.MaxValue"/> instead of wrapping.
/// </para>
/// <para>
/// <b>Stopping.</b> <see cref="Token"/> is cancelled only by the caller's token and the request deadline, never by an
/// exceeded allowance (a cost breach throws <see cref="RequestTooCostlyException"/> and is latched; whoever
/// orchestrates the stages cancels the rest when it observes it). <see cref="Checkpoint"/> classifies why the request
/// stopped, in this order: an exceeded allowance (<see cref="RequestTooCostlyException"/>, sticky: it can never be
/// swallowed into a successful finish), the original caller's cancellation (<see cref="OperationCanceledException"/>
/// carrying the caller's token), then the request deadline (<see cref="RequestDeadlineExceededException"/>). A new
/// breach is recorded only if none of these already applies, so a chunk or attempt that arrives after the request
/// stopped never masks the actual stop. Call <see cref="Checkpoint"/> when catching an
/// <see cref="OperationCanceledException"/> and once more at the end of the operation before reporting success. When
/// it returns, the cancellation was not the request's own (a backend-call or sub-query timeout, say) and remains the
/// caller's to handle; those timeouts are never request expiry.
/// </para>
/// <para>
/// <b>Deadline.</b> Measured on the monotonic <see cref="TimeProvider.GetTimestamp"/> clock from construction, and
/// checked at every decision point; the request is expired when the elapsed time reaches the deadline, whether or not
/// a timer callback was delivered. A one-shot timer additionally cancels <see cref="Token"/> promptly. It is rearmed
/// from the remaining monotonic time in slices of at most 4294967294 ms (the platform timer limit, about 49.7 days),
/// so any configured deadline is honoured and none is shortened.
/// </para>
/// Thread-safe. One lock guards the counters, the classification and the timer; cancellation callbacks run outside it.
/// Members other than <see cref="Dispose"/> must not be used after disposal.
/// </remarks>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The integration test project exercises this type; the host project has no InternalsVisibleTo.")]
public sealed class RequestBudget : IDisposable
{
    private const double MaxTimerMilliseconds = uint.MaxValue - 1;

    private readonly object _gate = new();
    private readonly long _callAllowance;
    private readonly long _maxResponseBytes;
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _linked;
    private readonly ITimer _timer;

    private long _calls;
    private long _responseBytes;
    private BudgetResource? _breach;
    private bool _disposed;

    // The terminal timer callback is cancelling _linked. A source must not be disposed while it cancels, so Dispose hands
    // the disposal to that callback instead.
    private bool _cancelling;

    /// <summary>Initializes a new instance of the <see cref="RequestBudget"/> class and starts the request deadline.</summary>
    /// <param name="limits">The validated limits; <c>BackendCallsPerShard</c> and <c>MaxResponseBytes</c> are read once.</param>
    /// <param name="timeouts">The validated timeouts; <c>Request</c> is read once.</param>
    /// <param name="shardCount">The number of configured shards; the call allowance is <c>BackendCallsPerShard</c> times this.</param>
    /// <param name="timeProvider">The clock and timer source.</param>
    /// <param name="callerToken">The original caller's cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/>, <paramref name="timeouts"/> or <paramref name="timeProvider"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="shardCount"/> is not positive.</exception>
    public RequestBudget(
        LimitOptions limits,
        TimeoutOptions timeouts,
        int shardCount,
        TimeProvider timeProvider,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shardCount);

        _callAllowance = (long)limits.BackendCallsPerShard * shardCount;
        _maxResponseBytes = limits.MaxResponseBytes;
        _deadline = timeouts.Request;
        _time = timeProvider;
        _callerToken = callerToken;
        _startTimestamp = timeProvider.GetTimestamp();

        // Arm only once the timer and token source are both assigned.
        _timer = timeProvider.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _timer.Change(NextSlice(_deadline - _time.GetElapsedTime(_startTimestamp)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Gets the token every backend call and stage of the request must observe. Cancelled by caller cancellation and by
    /// the request deadline, not by an exceeded allowance; use <see cref="Checkpoint"/> to learn which.
    /// </summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>Gets the backend call attempts granted so far. Refused attempts are not counted.</summary>
    public long BackendCalls
    {
        get
        {
            lock (_gate)
            {
                return _calls;
            }
        }
    }

    /// <summary>Gets the response-body bytes charged so far, saturating at <see cref="long.MaxValue"/>. Not clamped at the limit.</summary>
    public long ResponseBytes
    {
        get
        {
            lock (_gate)
            {
                return _responseBytes;
            }
        }
    }

    /// <summary>Charges one backend call attempt. Call it immediately before each actual send; do not send if it throws.</summary>
    /// <exception cref="RequestTooCostlyException">The allowance was already exceeded, or this attempt is one beyond it; the attempt is not charged.</exception>
    /// <exception cref="OperationCanceledException">The original caller cancelled; the attempt is not charged.</exception>
    /// <exception cref="RequestDeadlineExceededException">The request deadline passed; the attempt is not charged.</exception>
    public void BeforeBackendCall()
    {
        Exception? stop;
        lock (_gate)
        {
            stop = FindStop();
            if (stop is null)
            {
                if (_calls < _callAllowance)
                {
                    _calls++;
                }
                else
                {
                    stop = RecordBreach(BudgetResource.BackendCalls);
                }
            }
        }

        if (stop is not null)
        {
            throw stop;
        }
    }

    /// <summary>
    /// Charges <paramref name="count"/> response-body bytes that were just consumed. The full count is charged even when the
    /// request has already stopped, and before any failure is raised.
    /// </summary>
    /// <param name="count">The size of the consumed chunk; zero is allowed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="RequestTooCostlyException">The total now exceeds the limit, or an allowance was exceeded earlier.</exception>
    /// <exception cref="OperationCanceledException">The original caller cancelled.</exception>
    /// <exception cref="RequestDeadlineExceededException">The request deadline passed.</exception>
    public void OnResponseBytesConsumed(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Exception? stop;
        lock (_gate)
        {
            // Decided against the remaining allowance before the total saturates, so a limit of long.MaxValue is exact.
            bool crosses = count > _maxResponseBytes - _responseBytes;
            _responseBytes = count > long.MaxValue - _responseBytes ? long.MaxValue : _responseBytes + count;

            stop = FindStop();
            if (stop is null && crosses)
            {
                stop = RecordBreach(BudgetResource.ResponseBytes);
            }
        }

        if (stop is not null)
        {
            throw stop;
        }
    }

    /// <summary>
    /// Throws if the request has stopped for a reason of its own; returns when it has not. See the type remarks for the
    /// order of classification and when to call it.
    /// </summary>
    /// <exception cref="RequestTooCostlyException">An allowance was exceeded.</exception>
    /// <exception cref="OperationCanceledException">The original caller cancelled; <see cref="OperationCanceledException.CancellationToken"/> is the caller's token.</exception>
    /// <exception cref="RequestDeadlineExceededException">The request deadline passed.</exception>
    public void Checkpoint()
    {
        Exception? stop;
        lock (_gate)
        {
            stop = FindStop();
        }

        if (stop is not null)
        {
            throw stop;
        }
    }

    /// <summary>Releases the deadline timer and the linked token source. Safe to call more than once.</summary>
    public void Dispose()
    {
        bool disposeSource;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            disposeSource = !_cancelling;
        }

        if (disposeSource)
        {
            _linked.Dispose();
        }
    }

    // Rearm if the last slice was shorter than the remaining deadline. Cancel outside the lock because
    // cancellation runs registrations synchronously.
    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            TimeSpan remaining = _deadline - _time.GetElapsedTime(_startTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(NextSlice(remaining), Timeout.InfiniteTimeSpan);
                return;
            }

            _cancelling = true;
        }

        bool disposeSource;
        try
        {
            _linked.Cancel();
        }
        finally
        {
            lock (_gate)
            {
                _cancelling = false;
                disposeSource = _disposed;
            }

            if (disposeSource)
            {
                _linked.Dispose();
            }
        }
    }

    // The longest wait the platform timer accepts, rounded up to whole milliseconds so it never fires before the time is up.
    private static TimeSpan NextSlice(TimeSpan remaining) =>
        TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(remaining.TotalMilliseconds), 0, MaxTimerMilliseconds));

    // Must hold _gate. The request's own reason to stop, in classification order, or null. The exception is thrown by the
    // caller after releasing the lock.
    private Exception? FindStop()
    {
        if (_breach is { } resource)
        {
            return CostException(resource);
        }

        if (_callerToken.IsCancellationRequested)
        {
            return new OperationCanceledException(_callerToken);
        }

        return _time.GetElapsedTime(_startTimestamp) >= _deadline ? new RequestDeadlineExceededException(_deadline) : null;
    }

    // Must hold _gate. Latches the first exceeded allowance and returns its exception.
    private RequestTooCostlyException RecordBreach(BudgetResource resource)
    {
        _breach = resource;
        return CostException(resource);
    }

    private RequestTooCostlyException CostException(BudgetResource resource) =>
        new(resource, resource == BudgetResource.BackendCalls ? _callAllowance : _maxResponseBytes);
}

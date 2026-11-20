using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// Starts the shard calls of one page execution: the frontier's needed calls, and bounded speculative first-page calls for
/// later streams (design spec §7.1, §7.8). Each started call is charged against the request's live budget.
/// </summary>
/// <remarks>
/// <para>
/// The frontier is the single caller of the public methods. Speculative workers run beside it; the lock guards the budget
/// and the claim of each speculative fetch, so exactly one claimant starts it. At most <c>maxParallelFetches - 1</c>
/// workers run, which keeps one call slot for the frontier, and a worker never spends the frontier's last budgeted call.
/// </para>
/// <para>
/// Every call's result is kept as a completed task and its failure observed, so a discarded failure is never an unobserved
/// task exception; only a needed result is turned into a page or an exception. <see cref="DisposeAsync"/> cancels and joins
/// the workers, and must run on every exit.
/// </para>
/// </remarks>
internal sealed class FetchScheduler : IAsyncDisposable
{
    private readonly IShardClient _client;
    private readonly int _budget;
    private readonly int _speculativeSlots;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _speculation;
    private readonly Lock _lock = new();
    private readonly List<SpeculativeFetch> _queue = [];
    private readonly Dictionary<ShardId, SpeculativeFetch> _byShard = [];
    private readonly List<Task> _workers = [];
    private int _nextInQueue;
    private int _started;

    public FetchScheduler(IShardClient client, int budget, int maxParallelFetches, CancellationToken caller)
    {
        _client = client;
        _budget = budget;
        _speculativeSlots = maxParallelFetches - 1;
        _caller = caller;
        _speculation = CancellationTokenSource.CreateLinkedTokenSource(caller);
    }

    /// <summary>Gets the calls that started. Read it after <see cref="DisposeAsync"/> for the final count.</summary>
    public int Started
    {
        get
        {
            lock (_lock)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// Queues one speculative first-page fetch per request, in order, and starts the workers that run them. Call it at most
    /// once; it does nothing when no slot is free for speculation.
    /// </summary>
    public void Speculate(IEnumerable<ShardSearchRequest> firstPages)
    {
        if (_speculativeSlots == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (ShardSearchRequest request in firstPages)
            {
                var fetch = new SpeculativeFetch(request);
                _queue.Add(fetch);
                _byShard.Add(request.Shard, fetch);
            }
        }

        for (int i = Math.Min(_speculativeSlots, _queue.Count); i > 0; i--)
        {
            _workers.Add(Task.Run(WorkAsync));
        }
    }

    /// <summary>
    /// Gets a stream's first page for the frontier: the speculative result when a worker started it, otherwise a needed
    /// call with the queued request, or with <paramref name="remaining"/> when the stream was never queued.
    /// </summary>
    /// <returns>The page, and the request whose response it is.</returns>
    public async Task<(ShardPage Page, ShardSearchRequest Sent)> FirstPageAsync(ShardStream stream, int remaining)
    {
        SpeculativeFetch? fetch;
        bool claimedByWorker;
        lock (_lock)
        {
            _byShard.TryGetValue(stream.Shard, out fetch);
            claimedByWorker = fetch is { Claimed: true };
            if (!claimedByWorker)
            {
                ChargeNeeded();
                fetch?.Claim();
            }
        }

        ShardSearchRequest sent = fetch?.Request ?? stream.FirstPage(remaining);
        Task<ShardPage> completed = claimedByWorker
            ? await fetch!.Outcome
            : await CallAsync(sent, null, _caller);
        return (PageOrThrow(stream.Shard, completed), sent);
    }

    public Task<ShardPage> SearchAsync(ShardSearchRequest request) => NeededAsync(request.Shard, request, null);

    public Task<ShardPage> ContinueAsync(ShardCursor cursor) => NeededAsync(cursor.Shard, null, cursor);

    /// <summary>Cancels speculation and waits until every worker, and so every speculative call, has finished.</summary>
    public async ValueTask DisposeAsync()
    {
        await _speculation.CancelAsync();
        await Task.WhenAll(_workers);
        _speculation.Dispose();
    }

    private async Task<ShardPage> NeededAsync(ShardId shard, ShardSearchRequest? request, ShardCursor? cursor)
    {
        lock (_lock)
        {
            ChargeNeeded();
        }

        Task<ShardPage> completed = await CallAsync(request, cursor, _caller);
        return PageOrThrow(shard, completed);
    }

    private void ChargeNeeded()
    {
        if (_started >= _budget)
        {
            throw new FetchBudgetExhaustedException(_budget);
        }

        _started++;
    }

    private async Task WorkAsync()
    {
        while (TryClaimNext(out SpeculativeFetch? fetch))
        {
            fetch.Complete(await CallAsync(fetch.Request, null, _speculation.Token));
        }
    }

    // A worker stops at the first fetch it may not start, leaving that fetch for the frontier to claim.
    private bool TryClaimNext([NotNullWhen(true)] out SpeculativeFetch? fetch)
    {
        lock (_lock)
        {
            while (_nextInQueue < _queue.Count && !_speculation.IsCancellationRequested && _budget - _started > 1)
            {
                SpeculativeFetch candidate = _queue[_nextInQueue++];
                if (!candidate.Claimed)
                {
                    candidate.Claim();
                    _started++;
                    fetch = candidate;
                    return true;
                }
            }
        }

        fetch = null;
        return false;
    }

    /// <summary>Turns a needed call's completed task into its page, or into the exception the page fails with.</summary>
    private ShardPage PageOrThrow(ShardId shard, Task<ShardPage> completed)
    {
        try
        {
            return completed.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_caller.IsCancellationRequested)
        {
            _caller.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ShardFailureException(shard, ShardFailureReason.Timeout, exception);
        }
        catch (Exception exception)
        {
            throw new ShardFailureException(shard, ShardFailureReason.Failed, exception);
        }
    }

    /// <summary>Runs one client call to completion and returns it as a completed task, whatever its outcome.</summary>
    private async Task<Task<ShardPage>> CallAsync(ShardSearchRequest? request, ShardCursor? cursor, CancellationToken cancellationToken)
    {
        Task<ShardPage> call = InvokeAsync(request, cursor, cancellationToken);
        await ((Task)call).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Reading the exception marks a failure as observed, so a discarded one never reaches UnobservedTaskException.
        _ = call.Exception;
        return call;
    }

    // An async method turns a client that throws before returning its task into a faulted task.
    private async Task<ShardPage> InvokeAsync(ShardSearchRequest? request, ShardCursor? cursor, CancellationToken cancellationToken) =>
        request is not null
            ? await _client.SearchAsync(request, cancellationToken)
            : await _client.ContinueAsync(cursor!, cancellationToken);

    /// <summary>A speculative first-page fetch. Exactly one claimant starts it; a worker claimant publishes its outcome.</summary>
    private sealed class SpeculativeFetch(ShardSearchRequest request)
    {
        private readonly TaskCompletionSource<Task<ShardPage>> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ShardSearchRequest Request { get; } = request;

        /// <summary>Gets whether a claimant has started, or is about to start, this fetch. Guarded by the scheduler's lock.</summary>
        public bool Claimed { get; private set; }

        /// <summary>Gets the completed call, once the worker that claimed this fetch has run it.</summary>
        public Task<Task<ShardPage>> Outcome => _outcome.Task;

        public void Claim() => Claimed = true;

        public void Complete(Task<ShardPage> completed) => _outcome.SetResult(completed);
    }
}

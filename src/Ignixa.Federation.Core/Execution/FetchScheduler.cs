using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// Starts the shard calls of one page execution and charges each against the request's live call budget (design spec §7.8).
/// </summary>
/// <remarks>
/// Every call's result is kept as a completed task and its failure observed, so a discarded failure is never an unobserved
/// task exception; only a needed result is turned into a page or an exception.
/// </remarks>
internal sealed class FetchScheduler
{
    private readonly IShardClient _client;
    private readonly int _budget;
    private readonly CancellationToken _caller;
    private readonly Lock _lock = new();
    private int _started;

    public FetchScheduler(IShardClient client, int budget, CancellationToken caller)
    {
        _client = client;
        _budget = budget;
        _caller = caller;
    }

    /// <summary>Gets the calls that started.</summary>
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

    public Task<ShardPage> SearchAsync(ShardSearchRequest request) => NeededAsync(request.Shard, request, null);

    public Task<ShardPage> ContinueAsync(ShardCursor cursor) => NeededAsync(cursor.Shard, null, cursor);

    private async Task<ShardPage> NeededAsync(ShardId shard, ShardSearchRequest? request, ShardCursor? cursor)
    {
        lock (_lock)
        {
            if (_started >= _budget)
            {
                throw new FetchBudgetExhaustedException(_budget);
            }

            _started++;
        }

        Task<ShardPage> call = await CallAsync(request, cursor, _caller);
        return PageOrThrow(shard, call);
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
}

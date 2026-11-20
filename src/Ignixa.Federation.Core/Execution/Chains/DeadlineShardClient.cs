using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Bounds every call to an <see cref="IShardClient"/> by <c>Federation:Timeouts:BackendCall</c>, inside the token the caller
/// passes. A call that ends after its deadline or the caller's token fired surfaces as an
/// <see cref="OperationCanceledException"/>, which <see cref="FetchScheduler"/> turns into a shard timeout or the caller's
/// cancellation.
/// </summary>
/// <remarks>
/// The wrapper never abandons a call: it cancels the call's token and awaits the call to its end, because cancellation is
/// cooperative under the <see cref="IShardClient"/> contract and work still running after its caller returns could outlive
/// the request that owns it. A client that ignores cancellation therefore holds its caller until it returns. Once the token
/// has fired, cancellation wins over whatever the call reports: a late page never counts, and a late failure is reported as
/// the cancellation that caused it.
/// </remarks>
internal sealed class DeadlineShardClient(IShardClient inner, TimeSpan callTimeout) : IShardClient
{
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken) =>
        RunAsync(token => inner.SearchAsync(request, token), cancellationToken);

    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
        RunAsync(token => inner.ContinueAsync(cursor, token), cancellationToken);

    private async Task<ShardPage> RunAsync(Func<CancellationToken, Task<ShardPage>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(callTimeout);
        try
        {
            ShardPage page = await call(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return page;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && deadline.IsCancellationRequested)
        {
            throw new OperationCanceledException("The shard call was cancelled.", exception, deadline.Token);
        }
    }
}

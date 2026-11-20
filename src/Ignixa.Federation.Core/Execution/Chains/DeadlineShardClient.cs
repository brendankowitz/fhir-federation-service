using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Bounds every call to an <see cref="IShardClient"/> by <c>Federation:Timeouts:BackendCall</c>, inside the token the caller
/// passes. A call that ends by its deadline or the caller's token surfaces as an <see cref="OperationCanceledException"/>,
/// which <see cref="FetchScheduler"/> turns into a shard timeout or the caller's cancellation.
/// </summary>
/// <remarks>
/// A client that ignores cancellation is abandoned rather than awaited: its call keeps running detached and its eventual
/// failure is observed and discarded, so a deadline holds even against such a client, and the scheduler's join completes.
/// A page that arrives after the deadline or cancellation is discarded too, so a late success never counts.
/// </remarks>
internal sealed class DeadlineShardClient(IShardClient inner, TimeSpan callTimeout) : IShardClient
{
    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken) =>
        RunAsync(token => inner.SearchAsync(request, token), cancellationToken);

    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken) =>
        RunAsync(token => inner.ContinueAsync(cursor, token), cancellationToken);

    private static async Task<ShardPage> StartAsync(Func<CancellationToken, Task<ShardPage>> call, CancellationToken token) =>
        await call(token);

    private async Task<ShardPage> RunAsync(Func<CancellationToken, Task<ShardPage>> call, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(callTimeout);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Registered before the client registers its own callbacks. Cancellation runs the newest callbacks first, so a
        // client that honours cancellation normally completes before its call is abandoned.
        await using CancellationTokenRegistration registration = deadline.Token.Register(() => abandoned.TrySetResult());
        Task<ShardPage> running = StartAsync(call, deadline.Token);
        if (await Task.WhenAny(running, abandoned.Task) != running)
        {
            _ = running.ContinueWith(
                static detached => _ = detached.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            deadline.Token.ThrowIfCancellationRequested();
        }

        ShardPage page = await running;
        deadline.Token.ThrowIfCancellationRequested();
        return page;
    }
}

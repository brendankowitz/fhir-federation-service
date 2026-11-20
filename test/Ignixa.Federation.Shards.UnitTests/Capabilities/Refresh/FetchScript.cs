using System.Threading.Channels;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// A fetch seam whose every call waits for the test to finish it, so a test decides what each shard answers and when.
/// By default it keeps the seam's contract and observes cancellation; <c>honorsCancellation: false</c> models an
/// implementation that ignores its token.
/// </summary>
internal sealed class FetchScript(bool honorsCancellation = true)
{
    private readonly Channel<PendingFetch> _started = Channel.CreateUnbounded<PendingFetch>();

    public Task<ShardCapabilityEvidence> Fetch(ShardDescriptor shard, CancellationToken cancellationToken)
    {
        var call = new PendingFetch(shard, honorsCancellation, cancellationToken);
        _started.Writer.TryWrite(call);
        return call.Result;
    }

    /// <summary>Waits for the next call to start; it fails the test after a watchdog interval rather than hang.</summary>
    public async Task<PendingFetch> NextAsync() =>
        await _started.Reader.ReadAsync().AsTask().WaitAsync(Watchdog.Interval).ConfigureAwait(false);

    /// <summary>Takes a call that has already started, if any.</summary>
    public bool TryTakeStarted(out PendingFetch? call) => _started.Reader.TryRead(out call);
}

/// <summary>One started, unfinished fetch.</summary>
internal sealed class PendingFetch
{
    private readonly TaskCompletionSource<ShardCapabilityEvidence> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PendingFetch(ShardDescriptor shard, bool honorsCancellation, CancellationToken token)
    {
        Shard = shard;
        Token = token;
        if (honorsCancellation)
        {
            token.Register(() => _result.TrySetCanceled(token));
        }
    }

    public ShardDescriptor Shard { get; }

    public CancellationToken Token { get; }

    public Task<ShardCapabilityEvidence> Result => _result.Task;

    public void Succeed(ShardCapabilityEvidence evidence) => _result.SetResult(evidence);

    public void Fail(Exception exception) => _result.SetException(exception);
}

/// <summary>Bounds every wait of these tests; it only ever elapses when the code under test is broken.</summary>
internal static class Watchdog
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Yields until <paramref name="condition"/> holds, failing after the watchdog interval.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Interval)
            {
                throw new TimeoutException($"Gave up waiting for: {what}");
            }

            await Task.Yield();
        }
    }

    /// <summary>Lets already-runnable work run, so a test can show that something did not happen.</summary>
    public static async Task SettleAsync()
    {
        for (int i = 0; i < 100; i++)
        {
            await Task.Yield();
        }
    }
}

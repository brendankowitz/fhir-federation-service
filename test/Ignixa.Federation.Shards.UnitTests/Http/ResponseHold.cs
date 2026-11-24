namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// Holds one scripted response at <see cref="LoopbackFhirServer"/> after its request is recorded, until the test releases
/// it, so a test knows an attempt reached the wire and decides when that attempt ends. Pass <see cref="BeforeHead"/> as the
/// response's <c>beforeHead</c>.
/// </summary>
internal sealed class ResponseHold
{
    private static readonly TimeSpan ArrivalBound = TimeSpan.FromSeconds(15);

    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals the arrival, then waits for <see cref="Release"/> or for the server to stop.</summary>
    public async Task BeforeHead(CancellationToken stopping)
    {
        _arrived.TrySetResult();
        await _released.Task.WaitAsync(stopping);
    }

    /// <summary>Completes once the held request has been recorded; bounded, so a request that never arrives fails the test.</summary>
    public Task ArrivalAsync(CancellationToken cancellationToken) => _arrived.Task.WaitAsync(ArrivalBound, cancellationToken);

    /// <summary>Lets the server send the held response.</summary>
    public void Release() => _released.TrySetResult();
}

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The proof-backed source of the capabilities view that <see cref="CapabilityPublicationWorker"/> publishes.
/// </summary>
/// <remarks>
/// The source owns its evidence: it maps incomplete or contradictory evidence and expected probe refusals to
/// <see langword="null"/>, and orders each admission's commit against replacement of that evidence. The worker calls it
/// serially, never concurrently with itself, and never while holding its own lock.
/// </remarks>
internal interface ICapabilityAdmissionSource
{
    /// <summary>Tries to admit the current evidence as a view to serve.</summary>
    /// <param name="cancellationToken">Cancelled when the worker stops; the result is then ignored, even if valid.</param>
    /// <returns>The admission, or <see langword="null"/> for an expected refusal that the worker retries after its cadence.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>Any other exception is a defect: it stops the worker, which leaves the host cold.</remarks>
    ValueTask<CapabilityAdmission?> TryAdmitAsync(CancellationToken cancellationToken);
}

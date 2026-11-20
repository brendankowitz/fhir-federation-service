using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// What the refresher knows about one shard's advertisement. Advertised is not certified: this reports neither readiness
/// nor that the shard behaves as it advertises.
/// </summary>
/// <remarks>Immutable. Missing evidence and retained evidence after a failed refresh are distinct states.</remarks>
public sealed class ShardCapabilityEntry
{
    internal ShardCapabilityEntry(ShardCapabilityEvidence? evidence, ShardCallFailure? failure)
    {
        Evidence = evidence;
        Failure = failure;
    }

    /// <summary>Gets the last advertisement successfully fetched, or <see langword="null"/> when none ever was.</summary>
    public ShardCapabilityEvidence? Evidence { get; }

    /// <summary>
    /// Gets why the latest fetch failed, or <see langword="null"/> when it has not run or succeeded. Only the failure
    /// classification is kept, never the exception or error body.
    /// </summary>
    public ShardCallFailure? Failure { get; }

    /// <summary>Gets a value indicating whether no advertisement was ever fetched, whether or not a fetch has failed.</summary>
    public bool IsIncomplete => Evidence is null;

    /// <summary>Gets a value indicating whether the <see cref="Evidence"/> is retained from before a failed fetch.</summary>
    public bool IsDegraded => Evidence is not null && Failure is not null;
}

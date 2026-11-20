using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// The outcome of one <c>_sort=_lastUpdated&amp;_count=1</c> and replay probe: one shard, one resource type and one
/// starting method (design spec §9.2).
/// </summary>
/// <remarks>
/// Only <see cref="LastUpdatedReplayProbe"/> creates instances. The result keeps no cursor, resource key, body or exception.
/// An observed result describes the one boundary and one redemption pair that were sampled; it does not certify ordering,
/// tie stability, precision or replay for the shard, its profile, other methods or types, or any other page.
/// </remarks>
public sealed class LastUpdatedReplayProbeResult
{
    internal LastUpdatedReplayProbeResult(
        ShardId shard,
        ResourceType type,
        ShardSearchMethod startMethod,
        NextLinkRedemption? redemption,
        LastUpdatedReplayProbeOutcome outcome)
    {
        Shard = shard;
        Type = type;
        StartMethod = startMethod;
        Redemption = redemption;
        Outcome = outcome;
    }

    /// <summary>Gets the probed shard.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the probed resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the requested starting method.</summary>
    public ShardSearchMethod StartMethod { get; }

    /// <summary>Gets the first page's cursor redemption policy, or <see langword="null"/> if no cursor was observed.</summary>
    public NextLinkRedemption? Redemption { get; }

    /// <summary>Gets what the probe showed.</summary>
    public LastUpdatedReplayProbeOutcome Outcome { get; }

    /// <summary>Gets a value indicating whether the sampled boundary and replay were observed as described by <see cref="LastUpdatedReplayProbeOutcome.Observed"/>.</summary>
    public bool IsObserved => Outcome == LastUpdatedReplayProbeOutcome.Observed;
}

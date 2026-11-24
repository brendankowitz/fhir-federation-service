namespace Ignixa.Federation.Shards.Probes;

/// <summary>What one <c>_sort=_lastUpdated</c> and replay probe of a shard showed, for one resource type and one starting method (design spec §9.2).</summary>
/// <remarks>Only <see cref="Observed"/> is evidence, and only for the one sampled boundary. Every other value shows nothing.</remarks>
public enum LastUpdatedReplayProbeOutcome
{
    /// <summary>An exchange was refused, failed, or was not an exact answer to what was sent. Nothing was shown. The default.</summary>
    ReadFailed,

    /// <summary>
    /// The first page held exactly one match and a <c>next</c> cursor; the same cursor, redeemed twice, returned exactly one
    /// distinct match each time with non-decreasing instants from the first page's, and the two redeemed entries agreed.
    /// </summary>
    Observed,

    /// <summary>The method is POST and the shard's profile does not offer POST search. Nothing was sent.</summary>
    MethodNotOffered,

    /// <summary>A page did not hold exactly one match, or the first page had no <c>next</c> cursor, so there was no boundary to observe.</summary>
    InsufficientRows,

    /// <summary>A match had no <c>meta.lastUpdated</c>, so its position cannot be compared.</summary>
    InstantMissing,

    /// <summary>The first redeemed match is the same resource as the first page's, so no boundary between two resources was observed.</summary>
    BoundaryNotDistinct,

    /// <summary>The first redeemed match is earlier, as an instant, than the first page's match.</summary>
    OrderNotMonotone,

    /// <summary>The two redemptions of the same cursor returned different entries.</summary>
    ReplayDiffered,
}

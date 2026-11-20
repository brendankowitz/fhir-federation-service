using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// The outcome of one strict-handling probe: one shard, one resource type and one wire method (design spec §6.2, §9.2).
/// </summary>
/// <remarks>
/// Only <see cref="StrictHandlingProbe"/> creates instances. The result keeps no query, response body or exception. It
/// says nothing about the shard's software, its profile, other methods or types, or whether the shard is ready.
/// </remarks>
public sealed class StrictHandlingProbeResult
{
    internal StrictHandlingProbeResult(ShardId shard, ResourceType type, ShardSearchMethod method, StrictHandlingProbeOutcome outcome)
    {
        Shard = shard;
        Type = type;
        Method = method;
        Outcome = outcome;
    }

    /// <summary>Gets the probed shard.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the probed resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the wire method the probe used. Evidence for one method says nothing about the other.</summary>
    public ShardSearchMethod Method { get; }

    /// <summary>Gets what the probe established.</summary>
    public StrictHandlingProbeOutcome Outcome { get; }

    /// <summary>Gets a value indicating whether the shard was shown to answer an unadvertised parameter with HTTP 400 by <see cref="Method"/> on <see cref="Type"/>.</summary>
    public bool IsProven => Outcome == StrictHandlingProbeOutcome.Proven;
}

namespace Ignixa.Federation.Core.Configuration;

/// <summary>Where a chain or <c>_has</c> join happens (design spec §7.5).</summary>
public enum ChainResolutionMode
{
    /// <summary>Join in the federation: sub-query the target on every shard, then rewrite each shard's query. Exact across shards.</summary>
    Distributed,

    /// <summary>Join inside each shard: forward the chain unchanged. Exact only when every reference on the path stays on its shard.</summary>
    Passthrough,
}

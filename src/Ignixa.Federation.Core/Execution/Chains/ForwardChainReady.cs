using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// The plan's primary requests, ready for <see cref="UnsortedPageExecutor.ExecuteAsync"/> with <see cref="PageSize"/> and
/// the <see cref="ForwardChainResolution.RemainingFetchBudget"/>.
/// </summary>
public sealed class ForwardChainReady : ForwardChainResolution
{
    internal ForwardChainReady(
        IReadOnlyList<ShardSearchRequest> streams,
        int pageSize,
        IReadOnlyDictionary<int, IReadOnlyList<ResourceKey>> witnesses,
        int remainingFetchBudget,
        long remainingPayloadBytes)
        : base(remainingFetchBudget, remainingPayloadBytes)
    {
        Streams = streams;
        PageSize = pageSize;
        Witnesses = witnesses;
    }

    /// <summary>
    /// Gets one primary request per shard, in registry order: the plan's backend parameters with each distributed chain
    /// replaced by that shard's spelling of its witness keys, already measured against the shard's request bound.
    /// </summary>
    public IReadOnlyList<ShardSearchRequest> Streams { get; }

    /// <summary>Gets the plan's page size.</summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the distinct witness keys of each distributed chain, by the chain's occurrence ordinal, in shard registry order
    /// and then ordinal id order. A plan without distributed chains has none.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<ResourceKey>> Witnesses { get; }
}

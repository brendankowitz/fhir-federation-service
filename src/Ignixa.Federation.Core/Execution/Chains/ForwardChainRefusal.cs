using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>The plan is refused, and no primary request is exposed. It carries no query text or identifiers.</summary>
public sealed class ForwardChainRefusal : ForwardChainResolution
{
    internal ForwardChainRefusal(ForwardChainRefusalReason reason, int ordinal, ShardId? shard, int remainingFetchBudget, long remainingPayloadBytes)
        : base(remainingFetchBudget, remainingPayloadBytes)
    {
        Reason = reason;
        Ordinal = ordinal;
        Shard = shard;
    }

    /// <summary>Gets why the plan is refused.</summary>
    public ForwardChainRefusalReason Reason { get; }

    /// <summary>Gets the ordinal of the occurrence refused, or -1 for the searched type, a result control or the primary request.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the shard whose page or request bound caused the refusal, or <see langword="null"/> when none did.</summary>
    public ShardId? Shard { get; }
}

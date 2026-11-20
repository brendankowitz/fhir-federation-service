namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>A distributed chain found no witness on any shard, so no primary resource can match and nothing more is sent.</summary>
public sealed class ForwardChainEmpty : ForwardChainResolution
{
    internal ForwardChainEmpty(int ordinal, int remainingFetchBudget, long remainingPayloadBytes)
        : base(remainingFetchBudget, remainingPayloadBytes)
    {
        Ordinal = ordinal;
    }

    /// <summary>Gets the ordinal of the chain occurrence with no witness.</summary>
    public int Ordinal { get; }
}

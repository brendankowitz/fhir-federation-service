namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>The primary searches left after resolving a plan's reverse chains.</summary>
public sealed class ReverseChainResolution
{
    internal ReverseChainResolution(IReadOnlyList<ReverseChainTarget> targets, int calls)
    {
        Targets = targets;
        Calls = calls;
    }

    /// <summary>Gets participating shards in registry order. When empty, return an empty result without a primary search.</summary>
    public IReadOnlyList<ReverseChainTarget> Targets { get; }

    /// <summary>Gets sub-query calls spent, including continuations, to charge against the request budget.</summary>
    public int Calls { get; }
}

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// The outcome of resolving a plan's distributed forward chains: <see cref="ForwardChainReady"/>,
/// <see cref="ForwardChainEmpty"/> or <see cref="ForwardChainRefusal"/>. Each carries what the request may still spend.
/// </summary>
public abstract class ForwardChainResolution
{
    private protected ForwardChainResolution(int remainingFetchBudget, long remainingPayloadBytes)
    {
        RemainingFetchBudget = remainingFetchBudget;
        RemainingPayloadBytes = remainingPayloadBytes;
    }

    /// <summary>Gets the shard calls the request may still start, after every witness call that started.</summary>
    public int RemainingFetchBudget { get; }

    /// <summary>Gets the decoded resource bytes the request may still read, after every witness page charged.</summary>
    public long RemainingPayloadBytes { get; }
}

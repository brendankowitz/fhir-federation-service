namespace Ignixa.Federation.Core.Execution;

/// <summary>Why a shard failed a federated page (design spec §8).</summary>
public enum ShardFailureReason
{
    /// <summary>The shard client threw. The inner exception is the client's.</summary>
    Failed,

    /// <summary>The shard client was cancelled while the caller was not, so the shard did not answer in time.</summary>
    Timeout,

    /// <summary>The shard returned a page that breaks the shard contract: a foreign or duplicate key, or a bad or repeated cursor.</summary>
    ContractViolation,
}

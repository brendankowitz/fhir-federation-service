using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>Optional thread-safe strategy for one distributed depth-one forward chain in a paged search.</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The host composition supplies the optional strategy.")]
public interface IDistributedForwardSearch
{
    /// <summary>Discovers witnesses and prepares the canonical primary streams without sending those primary requests.</summary>
    /// <param name="plan">The current admitted plan.</param>
    /// <param name="capabilities">The exact profile and ordered registry view used to plan the query.</param>
    /// <param name="readableTypes">The primary and target types the caller is allowed to read.</param>
    /// <param name="client">The only client to use, already bound to this request's physical call and byte budget.</param>
    /// <param name="limits">The host's validated resource and concurrency limits; do not modify them.</param>
    /// <param name="timeouts">The host's validated timeouts, including the witness phase timeout; do not modify them.</param>
    /// <param name="fits">The transport's side-effect-free exact fit check; preflight witness requests and all primary streams.</param>
    /// <param name="fetchAllowance">The maximum logical witness and primary calls remaining in this request.</param>
    /// <param name="payloadByteAllowance">The maximum decoded witness payload bytes remaining.</param>
    /// <param name="cancellationToken">The live request token including the overall deadline.</param>
    /// <returns>
    /// All primary streams in registry order with their canonical owner-specific rewrites and the remaining logical
    /// fetch allowance. An empty list means no witnesses; <see langword="null"/> means insufficient reference evidence.
    /// </returns>
    /// <exception cref="FetchBudgetExhaustedException">A call, payload, key, or fit limit prevents complete resolution.</exception>
    /// <exception cref="ShardFailureException">A required shard failed or violated the shard contract.</exception>
    /// <exception cref="TimeoutException">Witness discovery reached its phase deadline.</exception>
    /// <exception cref="OperationCanceledException">The live request was canceled.</exception>
    Task<(IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)?> ResolveAsync(
        QueryPlan plan,
        SearchCapabilities capabilities,
        IReadOnlySet<ResourceType> readableTypes,
        IShardClient client,
        LimitOptions limits,
        TimeoutOptions timeouts,
        Func<ShardSearchRequest, bool> fits,
        int fetchAllowance,
        long payloadByteAllowance,
        CancellationToken cancellationToken);

    /// <summary>
    /// Purely reconstructs the canonical ordered primary streams from the authenticated saved rewrites and current
    /// plan. Extract and validate the frozen evidence and rebuild; never simply return the saved requests or rediscover witnesses.
    /// </summary>
    /// <param name="plan">The current replanned, authorized search.</param>
    /// <param name="capabilities">The current exact profile and ordered registry view.</param>
    /// <param name="savedStreams">The authenticated saved primary requests; do not modify them.</param>
    /// <param name="maxChainKeys">The current distinct-witness cap.</param>
    /// <param name="fits">The current transport's side-effect-free exact fit check.</param>
    /// <returns>The reconstructed streams, or <see langword="null"/> if the old search cannot be faithfully continued.</returns>
    IReadOnlyList<ShardSearchRequest>? Rebuild(
        QueryPlan plan,
        SearchCapabilities capabilities,
        IReadOnlyList<ShardSearchRequest> savedStreams,
        int maxChainKeys,
        Func<ShardSearchRequest, bool> fits);
}

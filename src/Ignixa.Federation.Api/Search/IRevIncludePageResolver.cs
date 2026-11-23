using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>Resolves reverse includes for one emitted page. The host may explicitly register a thread-safe implementation.</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "The host composition supplies the optional resolver.")]
public interface IRevIncludePageResolver
{
    /// <summary>
    /// Resolves only the certified reverse includes whose source type is readable. The resolver must return all eligible
    /// includes within the limit, or fail the page; it must not use another transport or start detached work.
    /// </summary>
    /// <param name="plan">The exact current plan, which can also contain requests for denied source types.</param>
    /// <param name="emittedMatches">Only the authorized primary matches emitted on this page.</param>
    /// <param name="readableTypes">Only the plan's primary type and the authorized source types consulted by this stage.</param>
    /// <param name="client">The same request-bound client used for the primary page; it charges physical calls and bytes.</param>
    /// <param name="registry">The exact registry against which the query was planned.</param>
    /// <param name="maxParallelFetches">The configured concurrent backend call limit.</param>
    /// <param name="fits">The transport's side-effect-free fit check, applied to the exact requests before sending any.</param>
    /// <param name="includeLimit">The maximum number of distinct includes to return.</param>
    /// <param name="remainingFetches">The maximum number of additional shard calls still available.</param>
    /// <param name="cancellationToken">The live request token, including its deadline.</param>
    /// <returns>The completed include page, or <see langword="null"/> when needed reference evidence cannot certify an include request.</returns>
    /// <exception cref="FetchBudgetExhaustedException">The required work exceeds the allowed calls or a request cannot fit.</exception>
    /// <exception cref="ShardFailureException">A needed shard failed or broke the transport contract.</exception>
    /// <exception cref="OperationCanceledException">The live request was canceled.</exception>
    Task<RevIncludePageResult?> ResolveAsync(
        QueryPlan plan,
        IReadOnlyList<ShardEntry> emittedMatches,
        IReadOnlySet<ResourceType> readableTypes,
        IShardClient client,
        ShardRegistry registry,
        int maxParallelFetches,
        Func<ShardSearchRequest, bool> fits,
        int includeLimit,
        int remainingFetches,
        CancellationToken cancellationToken);
}

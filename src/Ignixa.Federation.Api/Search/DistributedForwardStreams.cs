using System.Net;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Search;

/// <summary>
/// The host's check of what an <see cref="IDistributedForwardSearch"/> strategy hands back: the boundary shape, never the
/// reference semantics, which the strategy owns.
/// </summary>
/// <remarks>
/// A stream set is canonical when it has one stream per registered owner in registry order, each for the primary type
/// and requested page size, sent with a verb the owner's profile supports, and each is exactly the plan's own backend
/// parameters with the one distributed occurrence replaced by a nonempty value. That rejects the raw chain (a fallback
/// that would join against whatever the owner happens to hold), an extra or missing occurrence, and any stream another
/// owner or registry would not have produced. The plan's rewriter is the only parameter-building code consulted.
/// </remarks>
internal static class DistributedForwardStreams
{
    /// <summary>Whether <paramref name="streams"/> is the canonical shape for <paramref name="plan"/> on <paramref name="registry"/>.</summary>
    /// <param name="registry">The current ordered registry.</param>
    /// <param name="plan">The current plan; exactly one occurrence is a distributed forward replacement.</param>
    /// <param name="pageSize">The requested page size every stream carries.</param>
    /// <param name="streams">The strategy's or the saved streams.</param>
    /// <param name="resumed">The saved position being resumed, or <see langword="null"/> for a new search.</param>
    public static bool IsCanonical(
        ShardRegistry registry, QueryPlan plan, int pageSize, IReadOnlyList<ShardSearchRequest> streams, UnsortedContinuation? resumed)
    {
        if (streams.Count != registry.Shards.Count)
        {
            return false;
        }

        PlannedOccurrence replaced = plan.Occurrences.Single(o => o.ReplacementKey is not null);
        int ordinal = replaced.Occurrence.Ordinal;
        IReadOnlyList<ParameterOccurrence> first = plan.BuildBackendParameters(new Dictionary<int, string> { [ordinal] = "a" });
        IReadOnlyList<ParameterOccurrence> second = plan.BuildBackendParameters(new Dictionary<int, string> { [ordinal] = "b" });
        int at = -1;
        for (int i = 0; i < first.Count; i++)
        {
            if (first[i] != second[i])
            {
                at = i;
            }
        }

        for (int i = 0; i < streams.Count; i++)
        {
            ShardDescriptor owner = registry.Shards[i];
            ShardSearchRequest stream = streams[i];
            if (stream.Shard != owner.Id
                || stream.Type != plan.Type
                || stream.PageSize != pageSize
                || (stream.Method == ShardSearchMethod.PostSearch && !owner.Profile.SupportsPostSearch)
                || stream.Method is not (ShardSearchMethod.Get or ShardSearchMethod.PostSearch)
                || stream.Parameters.Count != first.Count
                || !SameParameters(plan, ordinal, at, stream)
                || !Resumable(owner, stream, resumed))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the transport accepts every stream exactly as the pager will send it: at most the owner's own page size.
    /// </summary>
    public static bool AllFit(ShardRegistry registry, IReadOnlyList<ShardSearchRequest> streams, Func<ShardSearchRequest, bool> fits)
    {
        for (int i = 0; i < streams.Count; i++)
        {
            ShardSearchRequest stream = streams[i];
            int sent = Math.Min(stream.PageSize, registry.Shards[i].Profile.MaxCount);
            if (!fits(new ShardSearchRequest(stream.Shard, stream.Type, stream.Parameters, sent, stream.Method)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameParameters(QueryPlan plan, int ordinal, int at, ShardSearchRequest stream)
    {
        string replacement = WebUtility.UrlDecode(stream.Parameters[at].Value);
        if (replacement.Length == 0)
        {
            return false;
        }

        return plan.BuildBackendParameters(new Dictionary<int, string> { [ordinal] = replacement }).SequenceEqual(stream.Parameters);
    }

    /// <summary>The saved cursor must still be one this owner can redeem with the verb the stream carries.</summary>
    private static bool Resumable(ShardDescriptor owner, ShardSearchRequest stream, UnsortedContinuation? resumed) =>
        resumed is null
        || resumed.Shard != owner.Id
        || !((resumed.Search is { } sent && sent.PageSize > owner.Profile.MaxCount)
            || (resumed.Cursor is { } cursor && cursor.Redemption != owner.Profile.RedemptionFor(stream.Method)));
}

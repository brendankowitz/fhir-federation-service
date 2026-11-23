using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Rebuilds the primary requests <see cref="ForwardChainResolver"/> issued for one distributed forward chain from frozen
/// copies of them, without I/O, so a host can redeem a continuation without searching the chain's witnesses again.
/// </summary>
/// <remarks>
/// <para>
/// Replay recovers the witness keys from the first stream's generated reference list alone: it decodes that one pair once,
/// splits it on unescaped commas, undoes FHIR escapes and classifies each alternative relative to the first stream's shard
/// with the issuing plan's registry. Every alias and form of one physical key collapses to that key. The keys are put in
/// canonical order (owner registry ordinal, then id) and every shard's primary is rebuilt by the request builder initial
/// resolution uses: the certified forms of the plan, the plan's verbatim raw pairs, the same method choice and the same fit
/// measurement.
/// </para>
/// <para>
/// Success is not a proof that the frozen streams are genuine or consistent. The rest of the first stream and every other
/// stream are not read, so the caller must compare every rebuilt stream with its frozen counterpart for full exact equality
/// (shard, type, page size, method and every raw parameter in order) and must authenticate the frozen state itself. A
/// tampered, stale or non-canonical frozen set rebuilds to streams that differ from it; a set changed consistently on every
/// stream to another canonical key set is indistinguishable from an issued one and is rebuilt as is. Replay performs no
/// authorization, budget or grant check.
/// </para>
/// </remarks>
public static class ForwardChainReplay
{
    /// <summary>Tries to rebuild the primary requests for <paramref name="plan"/> from <paramref name="frozen"/>.</summary>
    /// <param name="plan">
    /// The issuing plan, whose registry, certifications and raw pairs are authoritative. It must hold exactly one distributed
    /// forward chain, with only kept ordinary predicates and removed controls besides it: no other chain of either direction,
    /// include, sort, total or count-only request.
    /// </param>
    /// <param name="frozen">The frozen primary requests, one per registry shard in registry order.</param>
    /// <param name="maxChainKeys">The most distinct physical keys the chain may carry; reading stops at the first key past it.</param>
    /// <param name="fitsRequest">
    /// Whether a shard accepts a primary request as first sent: the actual method, the verbatim parameters and the page size
    /// clamped to the shard's <see cref="BackendProfile.MaxCount"/>. It must be pure; it may be called once per shard.
    /// </param>
    /// <param name="streams">
    /// The rebuilt requests, one per registry shard in registry order, each with the plan's page size; <see langword="null"/>
    /// when the method returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when the plan is outside the replayable shape; <paramref name="frozen"/> is not one request of
    /// the plan's type, page size and parameter count per registry shard, in order; the first stream's generated pair does not
    /// decode once to a list of unversioned references to the chain's target type under a registered base; the list names
    /// more than <paramref name="maxChainKeys"/> distinct keys; or some shard cannot spell the keys or does not accept its
    /// request.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChainKeys"/> is not positive.</exception>
    public static bool TryRebuild(
        QueryPlan plan,
        IReadOnlyList<ShardSearchRequest> frozen,
        int maxChainKeys,
        Func<ShardSearchRequest, bool> fitsRequest,
        [NotNullWhen(true)] out IReadOnlyList<ShardSearchRequest>? streams)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(fitsRequest);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainKeys);

        streams = null;
        if (ReplacedChain(plan) is not { } chain
            || !MatchesRegistry(plan, frozen)
            || !TryReadKeys(plan, chain, frozen[0], maxChainKeys, out ResourceKey[]? keys))
        {
            return false;
        }

        var witnesses = new Dictionary<int, IReadOnlyList<ResourceKey>> { [chain.Occurrence.Ordinal] = keys };
        var rebuilt = new ShardSearchRequest[frozen.Count];
        for (int i = 0; i < rebuilt.Length; i++)
        {
            if (!ForwardChainPrimary.TryBuild(plan, plan.Registry.Shards[i], witnesses, fitsRequest, out ShardSearchRequest? primary, out _))
            {
                return false;
            }

            rebuilt[i] = primary;
        }

        streams = Array.AsReadOnly(rebuilt);
        return true;
    }

    /// <summary>Gets the plan's one distributed forward chain, or <see langword="null"/> when the plan is outside the replayable shape.</summary>
    private static PlannedOccurrence? ReplacedChain(QueryPlan plan)
    {
        if (plan.CountOnly || plan.IncludeTotal || plan.Sort is not null)
        {
            return null;
        }

        PlannedOccurrence? chain = null;
        foreach (PlannedOccurrence planned in plan.Occurrences)
        {
            switch (planned)
            {
                case { Kind: OccurrenceKind.ForwardChain, Action: OccurrenceAction.Replace } when chain is null:
                    chain = planned;
                    break;
                case { Kind: OccurrenceKind.Predicate, Action: OccurrenceAction.Keep }:
                case { Kind: OccurrenceKind.Control, Action: OccurrenceAction.Remove }:
                    break;
                default:
                    return null;
            }
        }

        return chain;
    }

    /// <summary>Gets whether <paramref name="frozen"/> is one request per registry shard, in order, shaped as the plan's primaries.</summary>
    private static bool MatchesRegistry(QueryPlan plan, IReadOnlyList<ShardSearchRequest> frozen)
    {
        IReadOnlyList<ShardDescriptor> shards = plan.Registry.Shards;
        if (frozen.Count != shards.Count)
        {
            return false;
        }

        int sent = plan.Occurrences.Count(o => o.Action != OccurrenceAction.Remove);
        for (int i = 0; i < shards.Count; i++)
        {
            if (frozen[i] is not { } request
                || request.Shard != shards[i].Id
                || request.Type != plan.Type
                || request.PageSize != plan.PageSize
                || request.Parameters.Count != sent)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Recovers the distinct physical keys from <paramref name="first"/>'s generated pair, in canonical order, stopping at the
    /// first malformed or unroutable alternative or the first key past <paramref name="maxChainKeys"/>.
    /// </summary>
    private static bool TryReadKeys(
        QueryPlan plan,
        PlannedOccurrence chain,
        ShardSearchRequest first,
        int maxChainKeys,
        [NotNullWhen(true)] out ResourceKey[]? keys)
    {
        keys = null;

        // The rewriter keeps every unremoved occurrence in order and puts the replacement in the chain's place.
        int position = plan.Occurrences
            .TakeWhile(o => o.Occurrence.Ordinal != chain.Occurrence.Ordinal)
            .Count(o => o.Action != OccurrenceAction.Remove);
        ParameterOccurrence generated = first.Parameters[position];
        if (SearchQuery.ParseForm($"{generated.Key}={generated.Value}").Query is not { Occurrences: [{ } pair] }
            || pair.Key != chain.ReplacementKey)
        {
            return false;
        }

        ReferencePath path = chain.Hop!.Path;
        var classifier = new ReferenceClassifier(plan.Registry);
        var distinct = new HashSet<ResourceKey>();
        for (int next = 0; next <= pair.Value.Length;)
        {
            if (!ReferenceOrList.TryReadAlternative(pair.Value, ref next, out string? alternative)
                || classifier.Classify(first.Shard, alternative) is not { Kind: ReferenceKind.Relative or ReferenceKind.KnownAbsolute, Target: { } key, Version: null }
                || key.Type != path.Target)
            {
                return false;
            }

            if (distinct.Add(key) && distinct.Count > maxChainKeys)
            {
                return false;
            }
        }

        keys = ForwardChainPrimary.CanonicalOrder(plan.Registry, distinct);
        return true;
    }
}

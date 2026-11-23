using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Builds one shard's primary request for a plan whose distributed forward chains have their witness keys. Initial
/// resolution and replay share it, so a rebuilt stream is spelled, sent and measured exactly as the one first issued.
/// </summary>
internal static class ForwardChainPrimary
{
    /// <summary>Tries to build <paramref name="shard"/>'s primary request.</summary>
    /// <param name="plan">The plan; every replaced occurrence is a distributed forward chain with an entry in <paramref name="witnesses"/>.</param>
    /// <param name="shard">The registry shard the request is sent to.</param>
    /// <param name="witnesses">Each chain's witness keys, by occurrence ordinal, in the order their alternatives are emitted.</param>
    /// <param name="fitsRequest">
    /// Whether the shard accepts the request as first sent: its actual method, verbatim parameters and the page size
    /// clamped to the shard's <see cref="BackendProfile.MaxCount"/>.
    /// </param>
    /// <param name="primary">The request, which keeps the plan's page size; <see langword="null"/> on refusal.</param>
    /// <param name="refusal">
    /// Why the request was not built: <see cref="ForwardChainRefusalReason.UnspellableReference"/> with the first chain
    /// whose keys the shard cannot spell, or <see cref="ForwardChainRefusalReason.RequestTooLarge"/> with ordinal -1.
    /// </param>
    /// <returns><see langword="false"/> on refusal.</returns>
    public static bool TryBuild(
        QueryPlan plan,
        ShardDescriptor shard,
        IReadOnlyDictionary<int, IReadOnlyList<ResourceKey>> witnesses,
        Func<ShardSearchRequest, bool> fitsRequest,
        [NotNullWhen(true)] out ShardSearchRequest? primary,
        out (ForwardChainRefusalReason Reason, int Ordinal) refusal)
    {
        primary = null;
        var replacements = new Dictionary<int, string>();
        foreach (PlannedOccurrence chain in plan.Occurrences.Where(o => o.ReplacementKey is not null))
        {
            int ordinal = chain.Occurrence.Ordinal;
            ReferencePathCertification certified = chain.Hop!.Certifications[shard.Id];
            if (!ReferenceOrList.TryRender(witnesses[ordinal], shard.Id, plan.Registry, certified.OwnSideForms, certified.ForeignForms, out string? orList))
            {
                refusal = (ForwardChainRefusalReason.UnspellableReference, ordinal);
                return false;
            }

            replacements.Add(ordinal, orList);
        }

        IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(replacements);
        int pageSize = plan.PageSize!.Value; // Callers refuse a count-only plan first.
        ShardSearchMethod method = PrimaryMethod(shard, parameters);
        if (!fitsRequest(new ShardSearchRequest(shard.Id, plan.Type, parameters, Math.Min(pageSize, shard.Profile.MaxCount), method)))
        {
            refusal = (ForwardChainRefusalReason.RequestTooLarge, -1);
            return false;
        }

        refusal = default;
        primary = new ShardSearchRequest(shard.Id, plan.Type, parameters, pageSize, method);
        return true;
    }

    /// <summary>
    /// Orders distinct witness keys canonically: by owner registry ordinal, then by id with ordinal comparison. Both initial
    /// resolution and replay emit alternatives in this order, so it is part of the frozen wire text.
    /// </summary>
    /// <param name="registry">The registry that owns every key.</param>
    /// <param name="keys">Distinct keys, each owned by a <paramref name="registry"/> shard.</param>
    /// <returns>The keys in canonical order.</returns>
    public static ResourceKey[] CanonicalOrder(ShardRegistry registry, IEnumerable<ResourceKey> keys) =>
    [
        .. keys
            .OrderBy(k => registry.TryGetById(k.Shard, out ShardDescriptor? owner)
                ? owner.Ordinal
                : throw new UnreachableException("Every witness key is owned by a registry shard."))
            .ThenBy(k => k.Id.Value, StringComparer.Ordinal),
    ];

    /// <summary>
    /// Chooses how the primary search for <paramref name="parameters"/> is sent. A GET-only shard is sent GET. A POST-capable
    /// shard is sent POST unless two pairs are equal after one form decode (key and value, case-sensitive): a POST backend may
    /// collapse them, so the search goes by GET, which keeps every pair. The raw parameters are never altered.
    /// </summary>
    private static ShardSearchMethod PrimaryMethod(ShardDescriptor shard, IReadOnlyList<ParameterOccurrence> parameters)
    {
        if (!shard.Profile.SupportsPostSearch)
        {
            return ShardSearchMethod.Get;
        }

        SearchQuery form = SearchQuery.ParseForm(QueryRewriter.Render(parameters)).Query
            ?? throw new InvalidOperationException("The completed primary parameters are not valid form pairs.");
        var decoded = new HashSet<(string Key, string Value)>();
        return form.Occurrences.All(o => decoded.Add((o.Key, o.Value))) ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
    }
}

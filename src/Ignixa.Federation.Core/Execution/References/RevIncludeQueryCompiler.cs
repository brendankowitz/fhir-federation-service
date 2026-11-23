using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.References;

/// <summary>
/// Compiles the standalone searches that answer one planned <c>_revinclude</c> for a page: one search of the hop's source
/// type per queried shard, by the bare reference parameter, listing the page's authorized targets.
/// </summary>
public static class RevIncludeQueryCompiler
{
    /// <summary>Compiles the searches for <paramref name="occurrence"/>, one per distinct shard in <paramref name="sourceOwners"/>.</summary>
    /// <param name="plan">The plan that admitted <paramref name="occurrence"/>; its registry spells the references.</param>
    /// <param name="occurrence">An occurrence of <paramref name="plan"/> planned as a reverse include.</param>
    /// <param name="authorizedPageTargets">
    /// The page's keys the caller may read, of the searched type; duplicates are one key. Only these keys are listed.
    /// </param>
    /// <param name="sourceOwners">
    /// The shards to query, as a set. The caller selects them: a source resource may live on a shard other than its target's,
    /// so they are not derived from <paramref name="authorizedPageTargets"/>.
    /// </param>
    /// <param name="pageSize">The wanted count per search; clamped to each queried shard's profile maximum. Greater than zero.</param>
    /// <param name="fits">
    /// Trusted, synchronous and free of side effects: whether a completed request, measured by its exact method, parameters
    /// and count, is within its shard's request ceiling.
    /// </param>
    /// <returns>
    /// Read-only requests in registry ordinal order of their shards, each already accepted by <paramref name="fits"/>; empty
    /// when there are no targets or no sources, without calling <paramref name="fits"/>.
    /// </returns>
    /// <remarks>
    /// Each request is a POST when the queried shard's profile supports it, otherwise a GET, with exactly one pair: the reference
    /// parameter and the OR list of every key, spelled in the forms that shard certified for this exact search. A request that
    /// does not fit is refused; none is split, retried, resent by another method or truncated, and no partial result returns.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An argument is not valid for <paramref name="plan"/>: the occurrence is not its reverse include, a target has another
    /// type or an owner outside the registry, a source is outside the registry, or <paramref name="pageSize"/> is not positive.
    /// </exception>
    /// <exception cref="RevIncludeCompilationException">
    /// A queried shard has no current proof of this search or no spelling for a key
    /// (<see cref="RevIncludeCompilationReason.UnsupportedEvidence"/>), or a request does not fit
    /// (<see cref="RevIncludeCompilationReason.TooCostly"/>). An exception thrown by <paramref name="fits"/> propagates unchanged.
    /// </exception>
    public static IReadOnlyList<ShardSearchRequest> Compile(
        QueryPlan plan,
        PlannedOccurrence occurrence,
        IReadOnlyCollection<ResourceKey> authorizedPageTargets,
        IReadOnlyCollection<ShardId> sourceOwners,
        int pageSize,
        Func<ShardSearchRequest, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(authorizedPageTargets);
        ArgumentNullException.ThrowIfNull(sourceOwners);
        ArgumentNullException.ThrowIfNull(fits);
        if (occurrence is not { Kind: OccurrenceKind.RevInclude, Hop: { } hop } || !plan.Occurrences.Any(o => ReferenceEquals(o, occurrence)))
        {
            throw new ArgumentException("The occurrence is not a reverse include of this plan.", nameof(occurrence));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "A page asks for at least one resource.");
        }

        ShardRegistry registry = plan.Registry;
        foreach (ResourceKey target in authorizedPageTargets)
        {
            if (target.Type != hop.Path.Target || !registry.TryGetById(target.Shard, out _))
            {
                throw new ArgumentException("A target is not a resource of the searched type on a registry shard.", nameof(authorizedPageTargets));
            }
        }

        var sources = new List<ShardDescriptor>();
        foreach (ShardId id in sourceOwners.Distinct())
        {
            sources.Add(registry.TryGetById(id, out ShardDescriptor? source)
                ? source
                : throw new ArgumentException("A source is not a registry shard.", nameof(sourceOwners)));
        }

        if (authorizedPageTargets.Count == 0 || sources.Count == 0)
        {
            return [];
        }

        ResourceKey[] keys =
        [
            .. authorizedPageTargets
                .Distinct()
                .OrderBy(k => registry.Shards.Single(s => s.Id == k.Shard).Ordinal)
                .ThenBy(k => k.Id.Value, StringComparer.Ordinal),
        ];
        ShardSearchRequest[] requests =
        [
            .. sources.OrderBy(s => s.Ordinal).Select(source => Request(hop, registry, source, keys, pageSize)),
        ];

        foreach (ShardSearchRequest request in requests)
        {
            if (!fits(request))
            {
                throw new RevIncludeCompilationException(RevIncludeCompilationReason.TooCostly, request.Shard);
            }
        }

        return Array.AsReadOnly(requests);
    }

    private static ShardSearchRequest Request(ReferenceHop hop, ShardRegistry registry, ShardDescriptor source, ResourceKey[] keys, int pageSize)
    {
        ReferencePath path = hop.Path;
        if (!hop.Certifications.TryGetValue(source.Id, out ReferencePathCertification? certified)
            || certified.Path != path
            || !certified.Operations.HasFlag(ReferencePathOperations.RevIncludeSearch)
            || certified.RevIncludeSearchShape != $"{path.Source.Value}?@{path}"
            || certified.CoveredRegistryVersion != registry.Version
            || !ReferenceOrList.TryRender(keys, source.Id, registry, certified.OwnSideForms, certified.ForeignForms, out string? orList))
        {
            throw new RevIncludeCompilationException(RevIncludeCompilationReason.UnsupportedEvidence, source.Id);
        }

        return new ShardSearchRequest(
            source.Id,
            path.Source,
            [QueryRewriter.Encode(path.Code, orList)],
            Math.Min(pageSize, source.Profile.MaxCount),
            source.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get);
    }
}

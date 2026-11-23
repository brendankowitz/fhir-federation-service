using System.Collections.ObjectModel;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Search;

/// <summary>A planned search: every occurrence classified and every result control resolved. Nothing has run.</summary>
public sealed class QueryPlan
{
    internal QueryPlan(
        ResourceType type,
        SearchQuery query,
        IList<PlannedOccurrence> occurrences,
        int? pageSize,
        LastUpdatedSort? sort,
        bool includeTotal,
        string countShape,
        ShardRegistry registry)
    {
        Type = type;
        Query = query;
        Occurrences = new ReadOnlyCollection<PlannedOccurrence>(occurrences);
        PageSize = pageSize;
        Sort = sort;
        IncludeTotal = includeTotal;
        CountShape = countShape;
        Registry = registry;
    }

    /// <summary>Gets the searched resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the original query for the self link. Use <see cref="BuildBackendParameters"/> for shard requests.</summary>
    public SearchQuery Query { get; }

    /// <summary>Gets one planned occurrence per query occurrence, in ordinal order.</summary>
    public IReadOnlyList<PlannedOccurrence> Occurrences { get; }

    /// <summary>Gets a value indicating whether only a count is wanted (<c>_count=0</c> or <c>_summary=count</c>).</summary>
    public bool CountOnly => PageSize is null;

    /// <summary>Gets the page size, bounded by the configured maximum, or <see langword="null"/> when <see cref="CountOnly"/>.</summary>
    public int? PageSize { get; }

    /// <summary>Gets the requested <c>_lastUpdated</c> order, or <see langword="null"/> when none was asked.</summary>
    public LastUpdatedSort? Sort { get; }

    /// <summary>Gets a value indicating whether <c>_total=accurate</c> or <c>_total=estimate</c> asked for an exact total.</summary>
    public bool IncludeTotal { get; }

    /// <summary>
    /// Gets the normalized shape every shard must certify to count this query exactly: <c>Type?</c> then the backend-facing
    /// parameter keys sorted ordinally and joined by <c>&amp;</c>, each followed by <c>=,</c> when it lists alternatives.
    /// </summary>
    public string CountShape { get; }

    /// <summary>
    /// Gets the registry whose shards, versioned certifications and reference forms this plan was proven against. An executor
    /// checks it is the very instance it serves before sending anything, so a plan never runs against topology it was not
    /// planned for. Internal, so it is neither serialized nor caller-supplied.
    /// </summary>
    internal ShardRegistry Registry { get; }

    /// <summary>Builds backend search parameters after all rewrites for one shard have been resolved.</summary>
    /// <param name="decodedReplacements">
    /// One nonempty value for each Replace ordinal, percent-decoded but in FHIR search syntax: the caller joins a generated
    /// OR list with unescaped commas and must FHIR-escape each component first (<c>\,</c>, <c>\|</c>, <c>\$</c>, <c>\\</c>),
    /// because the value is only percent-encoded here. For an empty match set, the executor skips that shard rather than
    /// building a request.
    /// </param>
    /// <returns>Backend predicates in original order, plus the certified sort when requested. The executor adds pagination and count controls.</returns>
    /// <remarks>
    /// For a distributed forward rewrite, evidence covers this ordered search. The executor must not add, reorder or
    /// alter its predicates or sort; it renders generated keys using the selected shard's certified forms and enforces
    /// the configured key bound before sending a request.
    /// </remarks>
    /// <exception cref="ArgumentException">A replacement is missing, empty, or supplied for an occurrence that is not replaced.</exception>
    public IReadOnlyList<ParameterOccurrence> BuildBackendParameters(IReadOnlyDictionary<int, string> decodedReplacements)
    {
        ArgumentNullException.ThrowIfNull(decodedReplacements);
        var rewriter = new QueryRewriter(Query);
        int required = 0;
        foreach (PlannedOccurrence planned in Occurrences)
        {
            int ordinal = planned.Occurrence.Ordinal;
            if (planned.ReplacementKey is { } key)
            {
                if (!decodedReplacements.TryGetValue(ordinal, out string? value) || string.IsNullOrEmpty(value))
                {
                    throw new ArgumentException("Every distributed occurrence requires a nonempty replacement.", nameof(decodedReplacements));
                }

                rewriter.Replace(ordinal, QueryRewriter.Encode(key, value));
                required++;
            }
            else if (planned.Action == OccurrenceAction.Remove)
            {
                rewriter.Drop(ordinal);
            }
        }

        if (required != decodedReplacements.Count)
        {
            throw new ArgumentException("A replacement ordinal does not identify a distributed occurrence.", nameof(decodedReplacements));
        }

        return rewriter.Build();
    }
}

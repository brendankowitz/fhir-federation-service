using System.Collections.ObjectModel;
using Ignixa.Federation.Core.Identity;

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
        string countShape)
    {
        Type = type;
        Query = query;
        Occurrences = new ReadOnlyCollection<PlannedOccurrence>(occurrences);
        PageSize = pageSize;
        Sort = sort;
        IncludeTotal = includeTotal;
        CountShape = countShape;
    }

    /// <summary>Gets the searched resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the query as parsed, the template for the self link and backend requests.</summary>
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
}

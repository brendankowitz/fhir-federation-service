using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// One page of a federated search sorted by <c>_lastUpdated</c>: matches in the merge's total order (design spec §7.3).
/// </summary>
public sealed class SortedPage
{
    internal SortedPage(IReadOnlyList<ShardEntry> matches, SortedContinuation? next, int fetches)
    {
        Matches = matches;
        Next = next;
        Fetches = fetches;
    }

    /// <summary>
    /// Gets the page's matches, at most the requested page size, in emission order: by <c>_lastUpdated</c> in the requested
    /// direction, then registry ordinal, then the shard's own order. Each is the shard's entry as returned, bytes unchanged.
    /// </summary>
    public IReadOnlyList<ShardEntry> Matches { get; }

    /// <summary>Gets where the following page resumes, or <see langword="null"/> when every stream is known to be exhausted.</summary>
    public SortedContinuation? Next { get; }

    /// <summary>Gets the shard calls that started for this page.</summary>
    public int Fetches { get; }
}

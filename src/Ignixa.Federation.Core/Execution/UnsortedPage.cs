using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>One page of an unsorted federated search, in registry-ordinal then backend order (design spec §7.1).</summary>
public sealed class UnsortedPage
{
    internal UnsortedPage(IReadOnlyList<ShardEntry> matches, UnsortedContinuation? next, int fetches)
    {
        Matches = matches;
        Next = next;
        Fetches = fetches;
    }

    /// <summary>Gets the page's matches, at most the requested page size, in emission order.</summary>
    public IReadOnlyList<ShardEntry> Matches { get; }

    /// <summary>Gets where the following page starts, or <see langword="null"/> when every stream is exhausted.</summary>
    public UnsortedContinuation? Next { get; }

    /// <summary>
    /// Gets the shard calls that started for this page, including speculative calls whose results were discarded. It may
    /// vary with timing when speculation runs.
    /// </summary>
    public int Fetches { get; }
}

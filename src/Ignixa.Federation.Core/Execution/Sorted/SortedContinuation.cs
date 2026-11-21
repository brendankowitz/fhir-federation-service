using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// Where a sorted page resumes: one checkpoint per participating owner, in registry order (design spec §7.4). Redemption
/// is re-execution: each live owner replays its saved input and continues after its last emission. It holds no resource
/// bytes and no buffered page.
/// </summary>
public sealed class SortedContinuation
{
    /// <summary>Initializes a new instance of the <see cref="SortedContinuation"/> class.</summary>
    /// <param name="type">The resource type every stream searches.</param>
    /// <param name="direction">The sort direction of the search.</param>
    /// <param name="pageSize">The page size of the search.</param>
    /// <param name="streams">One checkpoint per participating owner, exhausted owners included, in registry order.</param>
    public SortedContinuation(ResourceType type, LastUpdatedSort direction, int pageSize, IReadOnlyList<SortedStreamContinuation> streams)
    {
        Type = type;
        Direction = direction;
        PageSize = pageSize;
        Streams = streams;
    }

    /// <summary>Gets the resource type every stream searches.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the sort direction of the search.</summary>
    public LastUpdatedSort Direction { get; }

    /// <summary>Gets the page size of the search.</summary>
    public int PageSize { get; }

    /// <summary>Gets one checkpoint per participating owner, exhausted owners included, in registry order.</summary>
    public IReadOnlyList<SortedStreamContinuation> Streams { get; }
}

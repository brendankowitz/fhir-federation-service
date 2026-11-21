using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

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
    /// <param name="streams">One checkpoint per participating owner, exhausted owners included, in registry order; copied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> or <paramref name="streams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> is undefined or <paramref name="pageSize"/> is not positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="streams"/> is empty, contains <see langword="null"/>, repeats a shard, has every owner exhausted (that
    /// page has no continuation), holds a request, cursor or last emission of another type, or saves a preceding instant
    /// that comes after its last emission in <paramref name="direction"/>.
    /// </exception>
    public SortedContinuation(ResourceType type, LastUpdatedSort direction, int pageSize, IReadOnlyList<SortedStreamContinuation> streams)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(streams);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Undefined sort direction.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        SortedStreamContinuation[] copy = [.. streams];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A continuation needs at least one owner.", nameof(streams));
        }

        int sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        HashSet<ShardId> owners = [];
        foreach (SortedStreamContinuation stream in copy)
        {
            if (stream is null)
            {
                throw new ArgumentException("Streams must not contain null.", nameof(streams));
            }

            if (!owners.Add(stream.Shard))
            {
                throw new ArgumentException("Each owner must appear once.", nameof(streams));
            }

            ResourceType? streamType = stream.Search?.Type ?? stream.Cursor?.Type ?? stream.LastEmitted?.Key.Type;
            if (streamType is not null && streamType != type)
            {
                throw new ArgumentException("Every owner must search the continuation's resource type.", nameof(streams));
            }

            if (stream is { PreviousPageLastUpdated: { } preceding, LastEmitted: { } anchor }
                && sign * preceding.CompareTo(anchor.LastUpdated) > 0)
            {
                throw new ArgumentException("A preceding instant must not come after its last emission.", nameof(streams));
            }
        }

        if (copy.All(stream => stream.IsExhausted))
        {
            throw new ArgumentException("A page whose owners are all exhausted has no continuation.", nameof(streams));
        }

        Type = type;
        Direction = direction;
        PageSize = pageSize;
        Streams = Array.AsReadOnly(copy);
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

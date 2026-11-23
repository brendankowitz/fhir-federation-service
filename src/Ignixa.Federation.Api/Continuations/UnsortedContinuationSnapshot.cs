using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>Ordered primary-search templates, the original client count, and the next unsorted executor input.</summary>
internal sealed class UnsortedContinuationSnapshot
{
    // The global v1 ceiling (§7.3), not a particular instance's current configured limit.
    private const int MaxPageSize = 100;

    internal UnsortedContinuationSnapshot(
        IReadOnlyList<ShardSearchRequest> streams,
        int pageSize,
        UnsortedContinuation continuation)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(continuation);
        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new ArgumentException("The original page size must be from 1 to 100.", nameof(pageSize));
        }

        ShardSearchRequest[] copy = [.. streams];
        if (copy.Length == 0)
        {
            throw new ArgumentException("At least one stream is required.", nameof(streams));
        }

        ShardSearchRequest? resumed = null;
        var shards = new HashSet<ShardId>();
        foreach (ShardSearchRequest stream in copy)
        {
            if (stream is null || stream.Type != copy[0].Type || !shards.Add(stream.Shard))
            {
                throw new ArgumentException("Streams must search one type with one request per shard.", nameof(streams));
            }

            if (stream.Shard == continuation.Shard)
            {
                resumed = stream;
            }
        }

        if (resumed is null || !Matches(resumed, pageSize, continuation))
        {
            throw new ArgumentException("The continuation does not match the saved stream.", nameof(continuation));
        }

        Streams = Array.AsReadOnly(copy);
        PageSize = pageSize;
        Continuation = continuation;
    }

    internal IReadOnlyList<ShardSearchRequest> Streams { get; }

    internal int PageSize { get; }

    internal UnsortedContinuation Continuation { get; }

    private static bool Matches(ShardSearchRequest stream, int pageSize, UnsortedContinuation continuation)
    {
        // Core factories already qualify the anchor and require a positive replay count.
        if (continuation.Search is { } replay)
        {
            return replay.Type == stream.Type && replay.Method == stream.Method && replay.PageSize <= pageSize
                && replay.Parameters.SequenceEqual(stream.Parameters);
        }

        // A POST stream can use either policy; the executor checks the current profile on redemption.
        return continuation.Cursor is not { } cursor
            || (cursor.Type == stream.Type
                && (stream.Method == ShardSearchMethod.PostSearch || cursor.Redemption == NextLinkRedemption.FollowGet));
    }
}

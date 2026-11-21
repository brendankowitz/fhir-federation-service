using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// One owner's checkpoint in a <see cref="SortedContinuation"/>: the input to replay and the last match emitted from it
/// (design spec §7.4). It holds no resource bytes and no buffered page.
/// </summary>
/// <remarks>
/// Exactly one shape holds. <see cref="AtSearch"/> replays the exact request that was sent; <see cref="AtCursor"/> redeems
/// a backend cursor; both skip through <see cref="LastEmitted"/> when one is set. A live owner that emitted anything always
/// replays the input holding its last emission, even when that emission ended its backend page. <see cref="AtEnd"/> marks
/// an owner known to be exhausted: it is never fetched again, and its <see cref="LastEmitted"/> is ordering evidence only.
/// </remarks>
public sealed class SortedStreamContinuation
{
    private SortedStreamContinuation(
        ShardId shard,
        ShardSearchRequest? search,
        ShardCursor? cursor,
        bool isExhausted,
        SortedAnchor? lastEmitted,
        CursorCycleState cycleState,
        DateTimeOffset? previousPageLastUpdated)
    {
        Shard = shard;
        Search = search;
        Cursor = cursor;
        IsExhausted = isExhausted;
        LastEmitted = lastEmitted;
        CycleState = cycleState;
        PreviousPageLastUpdated = previousPageLastUpdated;
    }

    /// <summary>Gets the shard whose stream this checkpoint belongs to.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the request to replay verbatim, or <see langword="null"/> when this is not <see cref="AtSearch"/>.</summary>
    public ShardSearchRequest? Search { get; }

    /// <summary>Gets the cursor to redeem, or <see langword="null"/> when this is not <see cref="AtCursor"/>.</summary>
    public ShardCursor? Cursor { get; }

    /// <summary>Gets a value indicating whether the stream is known to be exhausted; that is, this is <see cref="AtEnd"/>.</summary>
    public bool IsExhausted { get; }

    /// <summary>
    /// Gets the last match emitted from the stream, or <see langword="null"/> when it emitted none. For a live stream the
    /// replayed input must return this exact pair; the resumed stream continues after it.
    /// </summary>
    public SortedAnchor? LastEmitted { get; }

    /// <summary>
    /// Gets the cycle detection of the stream's cursors at the saved input, including <see cref="Cursor"/>. It is
    /// <see cref="CursorCycleState.Initial"/> unless this is <see cref="AtCursor"/>.
    /// </summary>
    public CursorCycleState CycleState { get; }

    /// <summary>
    /// Gets the last <c>_lastUpdated</c> of the nonempty backend page before <see cref="Cursor"/>, so the replayed page is
    /// checked for order from its first entry; <see langword="null"/> when no earlier page held an entry.
    /// </summary>
    public DateTimeOffset? PreviousPageLastUpdated { get; }

    /// <summary>Resumes by replaying <paramref name="sent"/> exactly and skipping through <paramref name="lastEmitted"/> when set.</summary>
    /// <param name="sent">The request as it was sent, including its own count.</param>
    /// <param name="lastEmitted">The last match emitted from the response to <paramref name="sent"/>, or <see langword="null"/>.</param>
    /// <returns>The checkpoint.</returns>
    public static SortedStreamContinuation AtSearch(ShardSearchRequest sent, SortedAnchor? lastEmitted)
    {
        ArgumentNullException.ThrowIfNull(sent);
        return new(sent.Shard, sent, null, false, lastEmitted, CursorCycleState.Initial, null);
    }

    /// <summary>Resumes by redeeming <paramref name="cursor"/> and skipping through <paramref name="lastEmitted"/> when set.</summary>
    /// <param name="cursor">The backend cursor to redeem.</param>
    /// <param name="lastEmitted">The last match emitted from the page <paramref name="cursor"/> returns, or <see langword="null"/>.</param>
    /// <param name="cycleState">The stream's cycle detection after following <paramref name="cursor"/>.</param>
    /// <param name="previousPageLastUpdated">The last instant of the nonempty page before <paramref name="cursor"/>, if any.</param>
    /// <returns>The checkpoint.</returns>
    public static SortedStreamContinuation AtCursor(
        ShardCursor cursor,
        SortedAnchor? lastEmitted,
        CursorCycleState cycleState,
        DateTimeOffset? previousPageLastUpdated)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return new(cursor.Shard, null, cursor, false, lastEmitted, cycleState, previousPageLastUpdated);
    }

    /// <summary>Marks <paramref name="shard"/>'s stream as known to be exhausted.</summary>
    /// <param name="shard">The exhausted shard.</param>
    /// <param name="lastEmitted">The last match the stream emitted, or <see langword="null"/> when it emitted none.</param>
    /// <returns>The checkpoint.</returns>
    public static SortedStreamContinuation AtEnd(ShardId shard, SortedAnchor? lastEmitted) =>
        new(shard, null, null, true, lastEmitted, CursorCycleState.Initial, null);
}

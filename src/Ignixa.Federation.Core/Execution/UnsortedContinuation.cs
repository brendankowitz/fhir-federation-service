using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// Where an unsorted page resumes: the shard input to replay or redeem, and the last key already emitted from it (design
/// spec §7.4). It holds no resource bytes.
/// </summary>
/// <remarks>
/// Exactly one shape holds. <see cref="AtStart"/> starts a stream fresh. <see cref="AtSearch"/> replays the exact request
/// that was sent and skips through <see cref="LastEmitted"/>. <see cref="AtCursor"/> redeems a backend cursor, and skips
/// through <see cref="LastEmitted"/> when one is set; it also carries the stream's <see cref="CycleState"/>, because only a
/// stream that has followed a cursor can be in a cycle.
/// </remarks>
public sealed class UnsortedContinuation
{
    private UnsortedContinuation(
        ShardId shard,
        ShardSearchRequest? search,
        ShardCursor? cursor,
        ResourceKey? lastEmitted,
        CursorCycleState cycleState)
    {
        Shard = shard;
        Search = search;
        Cursor = cursor;
        LastEmitted = lastEmitted;
        CycleState = cycleState;
    }

    /// <summary>Gets the shard whose stream resumes; every stream before it is exhausted.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the request to replay verbatim, or <see langword="null"/> when this is not <see cref="AtSearch"/>.</summary>
    public ShardSearchRequest? Search { get; }

    /// <summary>Gets the cursor to redeem, or <see langword="null"/> when this is not <see cref="AtCursor"/>.</summary>
    public ShardCursor? Cursor { get; }

    /// <summary>
    /// Gets the last key emitted from the replayed input. The resumed page starts after it, and fails with
    /// <see cref="ContinuationAnchorMissingException"/> when the replay no longer returns it.
    /// </summary>
    public ResourceKey? LastEmitted { get; }

    /// <summary>
    /// Gets the cycle detection of the resumed stream's cursors, including <see cref="Cursor"/>. It is
    /// <see cref="CursorCycleState.Initial"/> unless this is <see cref="AtCursor"/>.
    /// </summary>
    public CursorCycleState CycleState { get; }

    /// <summary>Resumes at the first page of <paramref name="shard"/>'s stream.</summary>
    /// <param name="shard">The shard whose stream starts fresh.</param>
    /// <returns>The continuation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> is <see langword="null"/>.</exception>
    public static UnsortedContinuation AtStart(ShardId shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        return new UnsortedContinuation(shard, null, null, null, CursorCycleState.Initial);
    }

    /// <summary>Resumes by replaying <paramref name="sent"/> exactly and skipping through <paramref name="lastEmitted"/>.</summary>
    /// <param name="sent">The request as it was sent, including its own count.</param>
    /// <param name="lastEmitted">The last key emitted from the response to <paramref name="sent"/>.</param>
    /// <returns>The continuation.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sent"/> asks for a count only, or <paramref name="lastEmitted"/> is not of its shard and type.
    /// </exception>
    public static UnsortedContinuation AtSearch(ShardSearchRequest sent, ResourceKey lastEmitted)
    {
        ArgumentNullException.ThrowIfNull(sent);
        ArgumentNullException.ThrowIfNull(lastEmitted);
        if (sent.PageSize < 1)
        {
            throw new ArgumentException("A replayed request must ask for at least one result.", nameof(sent));
        }

        RequireAnchorOf(sent.Shard, sent.Type, lastEmitted);
        return new UnsortedContinuation(sent.Shard, sent, null, lastEmitted, CursorCycleState.Initial);
    }

    /// <summary>Resumes by redeeming <paramref name="cursor"/>, skipping through <paramref name="lastEmitted"/> when set.</summary>
    /// <param name="cursor">The backend cursor to redeem.</param>
    /// <param name="lastEmitted">
    /// The last key emitted from the page <paramref name="cursor"/> returns, or <see langword="null"/> when none was.
    /// </param>
    /// <param name="cycleState">
    /// The stream's cycle detection after following <paramref name="cursor"/>, as the page that issued this continuation
    /// left it. <see cref="CursorCycleState.Initial"/> forgets earlier cursors, so a cycle through them is found later.
    /// </param>
    /// <returns>The continuation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cursor"/> or <paramref name="cycleState"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="lastEmitted"/> is not of the cursor's shard and type.</exception>
    public static UnsortedContinuation AtCursor(ShardCursor cursor, ResourceKey? lastEmitted, CursorCycleState cycleState)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(cycleState);
        if (lastEmitted is not null)
        {
            RequireAnchorOf(cursor.Shard, cursor.Type, lastEmitted);
        }

        return new UnsortedContinuation(cursor.Shard, null, cursor, lastEmitted, cycleState);
    }

    private static void RequireAnchorOf(ShardId shard, ResourceType type, ResourceKey lastEmitted)
    {
        if (lastEmitted.Shard != shard || lastEmitted.Type != type)
        {
            throw new ArgumentException("The anchor must have the shard and type of the input it resumes.", nameof(lastEmitted));
        }
    }
}

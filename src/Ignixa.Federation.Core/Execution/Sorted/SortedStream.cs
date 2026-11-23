using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// One owner's working state during a sorted execution: the input being read, the buffered page with its next unread
/// entry, and the checkpoint a continuation saves. It lives for one execution and is driven by one coordinator.
/// </summary>
/// <remarks>
/// The checkpoint is the input that held the last emission, not the read position: a successor read ahead but never
/// emitted from must not displace it (design Q2), so the next page can verify the anchor before trusting later entries.
/// </remarks>
internal sealed class SortedStream
{
    private static readonly ShardPage Drained = new([], null, null);

    private readonly ShardStream _stream;
    private readonly ShardSearchRequest? _search;
    private readonly int _sign;
    private readonly IComparer<(DateTimeOffset Instant, int Ordinal)> _order;
    private readonly (DateTimeOffset Instant, int Ordinal)? _floor;
    private readonly HashSet<ShardCursor> _redeemed = [];
    private Input _input;
    private Input? _emittedFrom;
    private ShardPage? _page;
    private int _index;
    private SortedAnchor? _lastEmitted;
    private SortedAnchor? _replayedAnchor;

    /// <summary>Starts the stream at <paramref name="start"/>: a fresh first search, or a saved checkpoint to replay.</summary>
    /// <param name="stream">The owner's shard stream.</param>
    /// <param name="start">The input to read first.</param>
    /// <param name="direction">The sort direction.</param>
    /// <param name="order">The federation's emission order (design Q6).</param>
    /// <param name="floor">
    /// The previous federation page's last match, which no head may precede (design Q3), or <see langword="null"/> on a
    /// first page or after a page that emitted nothing.
    /// </param>
    public SortedStream(
        ShardStream stream,
        SortedStreamContinuation start,
        LastUpdatedSort direction,
        IComparer<(DateTimeOffset Instant, int Ordinal)> order,
        (DateTimeOffset Instant, int Ordinal)? floor)
    {
        _stream = stream;
        _sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        _order = order;
        _floor = floor;

        // A stream redeemed from a cursor never returns to its first search, so it has none.
        _search = start.Search;
        _input = new Input(start.Cursor, start.CycleState, start.PreviousPageLastUpdated);
        _lastEmitted = start.LastEmitted;
        _replayedAnchor = start.LastEmitted;
        _emittedFrom = start.LastEmitted is null ? null : _input;

        // The replayed cursor is this execution's first redemption, so a page that links back to it is a cycle.
        if (start.Cursor is { } replayed)
        {
            _redeemed.Add(replayed);
        }

        // An owner the previous page proved exhausted starts drained, so it is never fetched again.
        if (start.IsExhausted)
        {
            _page = Drained;
            _replayedAnchor = null;
        }
    }

    public int Ordinal => _stream.Ordinal;

    /// <summary>Gets the next entry the stream would emit, or <see langword="null"/> when its buffered page is drained.</summary>
    public ShardEntry? Head => _page is not null && _index < _page.Entries.Count ? _page.Entries[_index] : null;

    /// <summary>Gets a value indicating whether the stream has proved it holds nothing more.</summary>
    public bool IsExhausted => _page is { Next: null } && _index == _page.Entries.Count;

    /// <summary>Gets a value indicating whether the stream has neither a head nor proof of exhaustion.</summary>
    public bool NeedsFetch => _page is null || (Head is null && _page.Next is not null);

    /// <summary>
    /// Starts the call for the stream's next input: its first search, or the successor of its drained page. Following a
    /// successor is a committed forward transition, so it advances cycle detection and carries the drained page's last
    /// instant. The call is charged and started before this method first yields, so a batch can start several at once.
    /// </summary>
    /// <returns>The call's page, for <see cref="Accept"/>.</returns>
    public async Task<ShardPage> RequestAsync(FetchScheduler scheduler)
    {
        if (_page is { Next: { } next })
        {
            CursorCycleState cycle = _stream.Follow(_input.Cycle, next);
            _redeemed.Add(next);
            DateTimeOffset? preceding = _page.Entries.Count > 0 ? _page.Entries[^1].LastUpdated : _input.Preceding;
            _input = new Input(next, cycle, preceding);
        }

        return _input.Cursor is { } cursor ? await scheduler.ContinueAsync(cursor) : await scheduler.SearchAsync(_search!);
    }

    /// <summary>Validates the page the stream's current input returned and buffers it from the stream's next head.</summary>
    public void Accept(ShardPage page)
    {
        _stream.Validate(page, _redeemed);
        if (page.Entries.Any(entry => entry.LastUpdated is null))
        {
            throw new ShardFailureException(_stream.Shard, ShardFailureReason.ContractViolation, null);
        }

        // A moved anchor is missing even when its new instant is also out of order (design algorithm step 4).
        int start = _replayedAnchor is { } anchor ? IndexAfter(anchor, page) : 0;
        CheckOrder(page);
        _page = page;
        _index = start;
        _replayedAnchor = null;

        // Only eligible entries face the boundary; the replayed prefix legitimately precedes it. Checking every new
        // head, not just the first, is equivalent: the stream is monotonic and its own ordinal never changes.
        if (_floor is { } floor && Head is { } head && _order.Compare((head.LastUpdated!.Value, Ordinal), floor) < 0)
        {
            throw new ShardFailureException(_stream.Shard, ShardFailureReason.ContractViolation, null);
        }
    }

    /// <summary>
    /// Finds where a replayed page continues: just after the exact pair the previous page emitted last. Only the replayed
    /// input is searched; an anchor that moved to a later page is as missing as a deleted one (design Q3).
    /// </summary>
    private int IndexAfter(SortedAnchor anchor, ShardPage page)
    {
        for (int i = 0; i < page.Entries.Count; i++)
        {
            ShardEntry entry = page.Entries[i];
            if (entry.Key == anchor.Key && entry.LastUpdated == anchor.LastUpdated)
            {
                return i + 1;
            }
        }

        throw new ContinuationAnchorMissingException(_stream.Shard);
    }

    /// <summary>
    /// Rejects a page that is not monotonic in the sort direction from the preceding nonempty page's last instant,
    /// including entries this execution will never emit: a backend that returns them out of order cannot be trusted to
    /// have returned the ones before them in order (design Q5). Every entry has an instant by now.
    /// </summary>
    private void CheckOrder(ShardPage page)
    {
        DateTimeOffset? previous = _input.Preceding;
        foreach (ShardEntry entry in page.Entries)
        {
            DateTimeOffset instant = entry.LastUpdated!.Value;
            if (previous is { } before && _sign * instant.CompareTo(before) < 0)
            {
                throw new ShardFailureException(_stream.Shard, ShardFailureReason.ContractViolation, null);
            }

            previous = instant;
        }
    }

    /// <summary>Emits <see cref="Head"/> and makes its input the stream's checkpoint.</summary>
    public ShardEntry Emit()
    {
        ShardEntry head = Head!;
        _lastEmitted = new SortedAnchor(head.LastUpdated!.Value, head.Key);
        _emittedFrom = _input;
        _index++;
        return head;
    }

    /// <summary>Saves where the next page resumes this stream (design Q2).</summary>
    public SortedStreamContinuation Snapshot()
    {
        if (IsExhausted)
        {
            return SortedStreamContinuation.AtEnd(_stream.Shard, _lastEmitted);
        }

        Input at = _emittedFrom ?? _input;
        return at.Cursor is { } cursor
            ? SortedStreamContinuation.AtCursor(cursor, _lastEmitted, at.Cycle, at.Preceding)
            : SortedStreamContinuation.AtSearch(_search!, _lastEmitted);
    }

    /// <summary>
    /// An input of the stream: its cursor (or the first search when <see langword="null"/>), the cycle detection after
    /// following it, and the last instant of the nonempty page before it.
    /// </summary>
    private readonly record struct Input(ShardCursor? Cursor, CursorCycleState Cycle, DateTimeOffset? Preceding);
}

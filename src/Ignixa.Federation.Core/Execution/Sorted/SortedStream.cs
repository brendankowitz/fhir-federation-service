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
    private readonly ShardStream _stream;
    private readonly ShardSearchRequest _search;
    private readonly int _sign;
    private Input _input = new(null, CursorCycleState.Initial, null);
    private Input? _emittedFrom;
    private ShardPage? _page;
    private int _index;
    private SortedAnchor? _lastEmitted;

    public SortedStream(ShardStream stream, ShardSearchRequest search, LastUpdatedSort direction)
    {
        _stream = stream;
        _search = search;
        _sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
    }

    public int Ordinal => _stream.Ordinal;

    /// <summary>Gets the next entry the stream would emit, or <see langword="null"/> when its buffered page is drained.</summary>
    public ShardEntry? Head => _page is not null && _index < _page.Entries.Count ? _page.Entries[_index] : null;

    /// <summary>Gets a value indicating whether the stream has proved it holds nothing more.</summary>
    public bool IsExhausted => _page is { Next: null } && _index == _page.Entries.Count;

    /// <summary>Gets a value indicating whether the stream has neither a head nor proof of exhaustion.</summary>
    public bool NeedsFetch => _page is null || (Head is null && _page.Next is not null);

    /// <summary>
    /// Fetches the stream's next input: its first search, or the successor of its drained page. Following a successor
    /// is a committed forward transition, so it advances cycle detection and carries the drained page's last instant.
    /// </summary>
    public async Task FetchAsync(FetchScheduler scheduler)
    {
        if (_page is { Next: { } next })
        {
            CursorCycleState cycle = _stream.Follow(_input.Cycle, next);
            DateTimeOffset? preceding = _page.Entries.Count > 0 ? _page.Entries[^1].LastUpdated : _input.Preceding;
            _input = new Input(next, cycle, preceding);
        }

        ShardPage page = _input.Cursor is { } cursor ? await scheduler.ContinueAsync(cursor) : await scheduler.SearchAsync(_search);
        CheckOrder(page);
        _page = page;
        _index = 0;
    }

    /// <summary>
    /// Rejects a page with an entry that has no instant, or that is not monotonic in the sort direction from the
    /// preceding nonempty page's last instant, including entries this execution will never emit: a backend that returns
    /// them out of order cannot be trusted to have returned the ones before them in order (design Q5).
    /// </summary>
    private void CheckOrder(ShardPage page)
    {
        DateTimeOffset? previous = _input.Preceding;
        foreach (ShardEntry entry in page.Entries)
        {
            if (entry.LastUpdated is not { } instant || (previous is { } before && _sign * instant.CompareTo(before) < 0))
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
            : SortedStreamContinuation.AtSearch(_search, _lastEmitted);
    }

    /// <summary>
    /// An input of the stream: its cursor (or the first search when <see langword="null"/>), the cycle detection after
    /// following it, and the last instant of the nonempty page before it.
    /// </summary>
    private readonly record struct Input(ShardCursor? Cursor, CursorCycleState Cycle, DateTimeOffset? Preceding);
}

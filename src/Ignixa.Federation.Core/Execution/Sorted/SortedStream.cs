using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// One owner's working state during a sorted execution: the input being read, the buffered page with its next unread
/// entry, and the checkpoint a continuation saves. It lives for one execution and is driven by one coordinator.
/// </summary>
internal sealed class SortedStream
{
    private readonly ShardStream _stream;
    private readonly ShardSearchRequest _search;
    private ShardPage? _page;
    private int _index;
    private SortedAnchor? _lastEmitted;

    public SortedStream(ShardStream stream, ShardSearchRequest search)
    {
        _stream = stream;
        _search = search;
    }

    public int Ordinal => _stream.Ordinal;

    /// <summary>Gets the next entry the stream would emit, or <see langword="null"/> when its buffered page is drained.</summary>
    public ShardEntry? Head => _page is not null && _index < _page.Entries.Count ? _page.Entries[_index] : null;

    /// <summary>Gets a value indicating whether the stream has proved it holds nothing more.</summary>
    public bool IsExhausted => _page is { Next: null } && _index == _page.Entries.Count;

    /// <summary>Gets a value indicating whether the stream has neither a head nor proof of exhaustion.</summary>
    public bool NeedsFetch => _page is null;

    /// <summary>Fetches the stream's next input.</summary>
    public async Task FetchAsync(FetchScheduler scheduler)
    {
        _page = await scheduler.SearchAsync(_search);
        _index = 0;
    }

    /// <summary>Emits <see cref="Head"/> and records it as the stream's last emission.</summary>
    public ShardEntry Emit()
    {
        ShardEntry head = Head!;
        _lastEmitted = new SortedAnchor(head.LastUpdated!.Value, head.Key);
        _index++;
        return head;
    }

    /// <summary>Saves where the next page resumes this stream.</summary>
    public SortedStreamContinuation Snapshot() =>
        IsExhausted ? SortedStreamContinuation.AtEnd(_stream.Shard, _lastEmitted) : SortedStreamContinuation.AtSearch(_search, _lastEmitted);
}

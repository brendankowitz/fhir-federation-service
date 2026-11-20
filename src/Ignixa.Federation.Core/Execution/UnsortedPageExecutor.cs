using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution;

/// <summary>
/// Executes one page of an unsorted federated search (design spec §7.1, §7.4, §7.8). Matches are emitted in registry
/// ordinal order and, within a shard, in backend order; a shard is emitted only after every earlier shard is exhausted.
/// </summary>
/// <remarks>
/// The first live stream (the frontier) is fetched alone. When its first response leaves the page unfilled, every later
/// stream gets one bounded speculative first-page fetch while the frontier keeps following its <c>next</c> links. A
/// speculative result is used only when emission reaches its stream; otherwise it is discarded, failures included. The
/// executor is stateless between calls and safe to share.
/// </remarks>
public sealed class UnsortedPageExecutor
{
    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly int _maxParallelFetches;

    /// <summary>Initializes a new instance of the <see cref="UnsortedPageExecutor"/> class.</summary>
    /// <param name="client">The client that calls shards.</param>
    /// <param name="registry">The registry that orders shards and holds their backend profiles.</param>
    /// <param name="maxParallelFetches">
    /// The most shard calls one execution runs at once. One slot is always kept for the frontier, so 1 disables
    /// speculation.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="registry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxParallelFetches"/> is less than 1.</exception>
    public UnsortedPageExecutor(IShardClient client, ShardRegistry registry, int maxParallelFetches)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelFetches, 1);
        _client = client;
        _registry = registry;
        _maxParallelFetches = maxParallelFetches;
    }

    /// <summary>Executes one page.</summary>
    /// <param name="streams">
    /// One request per participating shard, all of one resource type, in any order. Each request's page size is ignored;
    /// the executor sets the count it sends.
    /// </param>
    /// <param name="pageSize">The most matches the page holds.</param>
    /// <param name="resumeFrom">Where the previous page ended, or <see langword="null"/> for the first page.</param>
    /// <param name="fetchBudget">The most shard calls this execution may start, speculative calls included.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The page; <see cref="UnsortedPage.Next"/> is <see langword="null"/> when every stream is exhausted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="streams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="streams"/> is empty, holds <see langword="null"/>, a shard the registry does not know, a shard twice
    /// or more than one resource type; or <paramref name="resumeFrom"/> does not belong to one of the streams.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageSize"/> is less than 1 or <paramref name="fetchBudget"/> is negative.
    /// </exception>
    /// <exception cref="ShardFailureException">A shard the page needs failed, timed out or broke the shard contract.</exception>
    /// <exception cref="ContinuationAnchorMissingException">The replayed input no longer returns the anchor.</exception>
    /// <exception cref="FetchBudgetExhaustedException">The page needs a shard call beyond <paramref name="fetchBudget"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<UnsortedPage> ExecuteAsync(
        IReadOnlyList<ShardSearchRequest> streams,
        int pageSize,
        UnsortedContinuation? resumeFrom,
        int fetchBudget,
        CancellationToken cancellationToken)
    {
        ShardStream[] ordered = Order(streams);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(fetchBudget);
        int index = resumeFrom is null ? 0 : IndexOf(ordered, resumeFrom);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = new List<ShardEntry>(pageSize);
        var scheduler = new FetchScheduler(_client, fetchBudget, _maxParallelFetches, cancellationToken);
        UnsortedContinuation? next;
        await using (scheduler)
        {
            next = await FillAsync(ordered, index, resumeFrom, matches, pageSize, scheduler);
        }

        // Read after the join, so the count includes every speculative call that started.
        return new UnsortedPage(matches, next, scheduler.Started);
    }

    /// <summary>
    /// Fills <paramref name="matches"/> from the frontier stream onward and says where the next page starts. The first
    /// frontier response that leaves the page unfilled starts the speculative fanout over every later stream, once.
    /// </summary>
    private static async Task<UnsortedContinuation?> FillAsync(
        ShardStream[] ordered,
        int index,
        UnsortedContinuation? resumeFrom,
        List<ShardEntry> matches,
        int pageSize,
        FetchScheduler scheduler)
    {
        var redeemed = new HashSet<ShardCursor>();
        ShardSearchRequest? search = resumeFrom?.Search;
        ShardCursor? cursor = resumeFrom?.Cursor;
        ResourceKey? anchor = resumeFrom?.LastEmitted;
        if (cursor is not null)
        {
            redeemed.Add(cursor);
        }

        bool fannedOut = false;
        while (index < ordered.Length)
        {
            ShardStream stream = ordered[index];
            ShardPage page;
            if (cursor is not null)
            {
                page = await scheduler.ContinueAsync(cursor);
            }
            else if (search is not null)
            {
                page = await scheduler.SearchAsync(search);
            }
            else
            {
                (page, search) = await scheduler.FirstPageAsync(stream, pageSize - matches.Count);
            }

            stream.Validate(page, redeemed);
            int start = 0;
            if (anchor is not null)
            {
                start = IndexOfAnchor(page, anchor, stream.Shard) + 1;
                anchor = null;
            }

            int taken = Math.Min(pageSize - matches.Count, page.Entries.Count - start);
            for (int i = start; i < start + taken; i++)
            {
                matches.Add(page.Entries[i]);
            }

            if (matches.Count == pageSize)
            {
                return ContinuationAfter(ordered, index, page, start + taken, cursor, search, matches[^1].Key);
            }

            if (!fannedOut)
            {
                fannedOut = true;
                int remaining = pageSize - matches.Count;
                scheduler.Speculate(ordered.Skip(index + 1).Select(later => later.FirstPage(remaining)));
            }

            if (page.Next is not null)
            {
                redeemed.Add(page.Next);
                cursor = page.Next;
                search = null;
            }
            else
            {
                index++;
                cursor = null;
                search = null;
            }
        }

        return null;
    }
    /// <summary>
    /// Says where the next page starts once this one is full. A fill inside a backend page replays the same input and skips
    /// through the last emitted key; a fill at the end of a backend page continues with its successor.
    /// </summary>
    private static UnsortedContinuation? ContinuationAfter(
        ShardStream[] ordered,
        int index,
        ShardPage page,
        int consumed,
        ShardCursor? cursor,
        ShardSearchRequest? search,
        ResourceKey last)
    {
        if (consumed < page.Entries.Count)
        {
            return cursor is not null ? UnsortedContinuation.AtCursor(cursor, last) : UnsortedContinuation.AtSearch(search!, last);
        }

        if (page.Next is not null)
        {
            return UnsortedContinuation.AtCursor(page.Next, null);
        }

        return index + 1 < ordered.Length ? UnsortedContinuation.AtStart(ordered[index + 1].Shard) : null;
    }

    private static int IndexOfAnchor(ShardPage page, ResourceKey anchor, ShardId shard)
    {
        for (int i = 0; i < page.Entries.Count; i++)
        {
            if (page.Entries[i].Key == anchor)
            {
                return i;
            }
        }

        throw new ContinuationAnchorMissingException(shard);
    }

    private static int IndexOf(ShardStream[] ordered, UnsortedContinuation resumeFrom)
    {
        for (int i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Shard == resumeFrom.Shard)
            {
                return ordered[i].Accepts(resumeFrom)
                    ? i
                    : throw new ArgumentException("The continuation does not match its shard's stream.", nameof(resumeFrom));
            }
        }

        throw new ArgumentException("The continuation's shard is not one of the streams.", nameof(resumeFrom));
    }

    private ShardStream[] Order(IReadOnlyList<ShardSearchRequest> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);
        if (streams.Count == 0)
        {
            throw new ArgumentException("At least one stream is required.", nameof(streams));
        }

        var ordered = new ShardStream[streams.Count];
        var shards = new HashSet<ShardId>();
        for (int i = 0; i < streams.Count; i++)
        {
            ShardSearchRequest stream = streams[i]
                ?? throw new ArgumentException("Streams must not contain null.", nameof(streams));
            if (stream.Type != streams[0]!.Type)
            {
                throw new ArgumentException("Every stream must search the same resource type.", nameof(streams));
            }

            if (!_registry.TryGetById(stream.Shard, out ShardDescriptor? descriptor))
            {
                throw new ArgumentException("Every stream's shard must be registered.", nameof(streams));
            }

            if (!shards.Add(stream.Shard))
            {
                throw new ArgumentException("Each shard may have one stream.", nameof(streams));
            }

            ordered[i] = new ShardStream(stream, descriptor);
        }

        Array.Sort(ordered, (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return ordered;
    }
}

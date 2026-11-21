using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Sorted;

/// <summary>
/// Executes one page of a federated search sorted by <c>_lastUpdated</c> (design spec §7.3, §7.4, §7.8). Every
/// participating stream is merged k-way: matches are emitted by instant in the requested direction, then registry ordinal,
/// then the shard's own order.
/// </summary>
/// <remarks>
/// Before each emission every live stream holds a head or proof that it is exhausted, so a match is emitted only when no
/// stream can still produce an earlier one. Empty and short backend pages are followed through their <c>next</c> links.
/// The executor is stateless between calls and safe to share.
/// </remarks>
public sealed class SortedPageExecutor
{
    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly int _maxParallelFetches;

    /// <summary>Initializes a new instance of the <see cref="SortedPageExecutor"/> class.</summary>
    /// <param name="client">The client that calls shards.</param>
    /// <param name="registry">The registry that orders shards and holds their backend profiles.</param>
    /// <param name="maxParallelFetches">The most shard calls one execution runs at once.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="registry"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxParallelFetches"/> is less than 1.</exception>
    public SortedPageExecutor(IShardClient client, ShardRegistry registry, int maxParallelFetches)
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
    /// One request per participating shard, all of one resource type, in any order. Each request carries the kept
    /// <c>_sort</c>; its page size is ignored, since the executor sets the count it sends.
    /// </param>
    /// <param name="direction">The sort direction every stream's request carries.</param>
    /// <param name="pageSize">The most matches the page holds.</param>
    /// <param name="resumeFrom">Where the previous page ended, or <see langword="null"/> for the first page.</param>
    /// <param name="fetchBudget">The most shard calls this execution may start.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The page; <see cref="SortedPage.Next"/> is <see langword="null"/> when every stream is known to be exhausted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="streams"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="streams"/> is empty, holds <see langword="null"/>, a shard the registry does not know, a shard twice
    /// or more than one resource type; or <paramref name="resumeFrom"/> belongs to another search: another type,
    /// direction, page size, owner list or order, a saved request that is not the owner's request at a count it accepts,
    /// or a saved cursor redeemed another way.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> is not defined, <paramref name="pageSize"/> is less than 1 or <paramref name="fetchBudget"/>
    /// is negative.
    /// </exception>
    /// <exception cref="ShardFailureException">
    /// A shard the page needs failed, timed out or broke the shard contract: a foreign or repeated key, a missing or
    /// out-of-order <c>_lastUpdated</c>, a foreign, repeated or cyclic <c>next</c> link, or a head that precedes the last
    /// match <paramref name="resumeFrom"/> emitted. No match is returned.
    /// </exception>
    /// <exception cref="ContinuationAnchorMissingException">
    /// A stream's replayed input no longer holds the exact match <paramref name="resumeFrom"/> emitted last from it, at its
    /// saved instant, so the page cannot resume without a skip or a duplicate.
    /// </exception>
    /// <exception cref="FetchBudgetExhaustedException">The page needs a shard call beyond <paramref name="fetchBudget"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<SortedPage> ExecuteAsync(
        IReadOnlyList<ShardSearchRequest> streams,
        LastUpdatedSort direction,
        int pageSize,
        SortedContinuation? resumeFrom,
        int fetchBudget,
        CancellationToken cancellationToken)
    {
        ShardStream[] ordered = Order(streams);
        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "The direction must be ascending or descending.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(fetchBudget);
        if (resumeFrom is not null)
        {
            CheckPlan(resumeFrom, ordered, streams[0].Type, direction, pageSize);
        }

        cancellationToken.ThrowIfCancellationRequested();
        int requestCount = FirstRequestCount(pageSize, ordered.Length);
        Comparer<(DateTimeOffset Instant, int Ordinal)> order = EmissionOrder(direction);
        (DateTimeOffset Instant, int Ordinal)? floor = resumeFrom is null ? null : Boundary(resumeFrom, ordered, order);
        SortedStream[] owners =
        [
            .. ordered.Select((stream, i) => new SortedStream(
                stream,
                resumeFrom?.Streams[i] ?? SortedStreamContinuation.AtSearch(stream.FirstPage(requestCount), null),
                direction,
                order,
                floor)),
        ];
        var matches = new List<ShardEntry>();
        var scheduler = new FetchScheduler(_client, fetchBudget, _maxParallelFetches, cancellationToken);
        await using (scheduler)
        {
            await MergeAsync(owners, order, pageSize, matches, scheduler);
        }

        SortedContinuation? next = owners.All(owner => owner.IsExhausted)
            ? null
            : new SortedContinuation(streams[0].Type, direction, pageSize, [.. owners.Select(owner => owner.Snapshot())]);
        return new SortedPage(matches, next, scheduler.Started);
    }

    /// <summary>
    /// Gives every owner a head or exhaustion proof, then repeatedly emits the head first in the total order and refills
    /// only that owner, until the page is full or every owner is exhausted.
    /// </summary>
    private async Task MergeAsync(
        SortedStream[] owners,
        Comparer<(DateTimeOffset Instant, int Ordinal)> order,
        int pageSize,
        List<ShardEntry> matches,
        FetchScheduler scheduler)
    {
        // Ordinal rounds: one input per unresolved owner per round, so no owner's leading empties delay another's head.
        // Each round runs in fixed ordinal batches, so which calls run together never depends on how fast shards answer.
        SortedStream[] unresolved = [.. owners.Where(owner => owner.NeedsFetch)];
        while (unresolved.Length > 0)
        {
            foreach (SortedStream[] batch in unresolved.Chunk(_maxParallelFetches))
            {
                await FetchAsync(batch, scheduler);
            }

            unresolved = [.. unresolved.Where(owner => owner.NeedsFetch)];
        }

        var heads = new PriorityQueue<SortedStream, (DateTimeOffset Instant, int Ordinal)>(order);
        foreach (SortedStream owner in owners)
        {
            Enqueue(heads, owner);
        }

        while (heads.TryDequeue(out SortedStream? winner, out _))
        {
            matches.Add(winner.Emit());
            if (matches.Count == pageSize)
            {
                return;
            }

            while (winner.NeedsFetch)
            {
                await FetchAsync([winner], scheduler);
            }

            Enqueue(heads, winner);
        }
    }

    /// <summary>
    /// Starts one call per owner of <paramref name="batch"/> in ordinal order without awaiting between starts, joins them
    /// all, then hands each owner its page in ordinal order. The first owner whose call failed or whose page is invalid
    /// throws, so which error wins never depends on which call finished first.
    /// </summary>
    private static async Task FetchAsync(SortedStream[] batch, FetchScheduler scheduler)
    {
        Task<ShardPage>[] calls = [.. batch.Select(owner => owner.RequestAsync(scheduler))];
        Task joined = Task.WhenAll(calls);
        await joined.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        foreach (Task<ShardPage> call in calls)
        {
            _ = call.Exception;
        }

        for (int i = 0; i < batch.Length; i++)
        {
            batch[i].Accept(await calls[i]);
        }
    }

    private static void Enqueue(PriorityQueue<SortedStream, (DateTimeOffset Instant, int Ordinal)> heads, SortedStream owner)
    {
        if (owner.Head is { } head)
        {
            heads.Enqueue(owner, (head.LastUpdated!.Value, owner.Ordinal));
        }
    }

    /// <summary>
    /// Asks each of <paramref name="streamCount"/> owners for twice its fair share of the page, at least 20 to bound
    /// round trips for skewed data and never more than the page itself (design Q4).
    /// </summary>
    private static int FirstRequestCount(int pageSize, int streamCount) =>
        (int)Math.Min(pageSize, Math.Max(((2L * pageSize) + streamCount - 1) / streamCount, 20L));

    /// <summary>Orders heads by instant in <paramref name="direction"/>, then ascending registry ordinal (design Q6).</summary>
    private static Comparer<(DateTimeOffset Instant, int Ordinal)> EmissionOrder(LastUpdatedSort direction)
    {
        int sign = direction == LastUpdatedSort.Ascending ? 1 : -1;
        return Comparer<(DateTimeOffset Instant, int Ordinal)>.Create((left, right) =>
        {
            int byInstant = sign * left.Instant.CompareTo(right.Instant);
            return byInstant != 0 ? byInstant : left.Ordinal.CompareTo(right.Ordinal);
        });
    }

    /// <summary>
    /// Recovers the previous federation page's last match: the furthest in emission order of every owner's last
    /// emission, exhausted owners included (design Q3). <see langword="null"/> when that page emitted nothing.
    /// </summary>
    private static (DateTimeOffset Instant, int Ordinal)? Boundary(
        SortedContinuation resumeFrom,
        ShardStream[] ordered,
        Comparer<(DateTimeOffset Instant, int Ordinal)> order)
    {
        (DateTimeOffset Instant, int Ordinal)? furthest = null;
        for (int i = 0; i < ordered.Length; i++)
        {
            if (resumeFrom.Streams[i].LastEmitted is { } emitted
                && (furthest is not { } current || order.Compare((emitted.LastUpdated, ordered[i].Ordinal), current) > 0))
            {
                furthest = (emitted.LastUpdated, ordered[i].Ordinal);
            }
        }

        return furthest;
    }

    /// <summary>
    /// Rejects a well-formed snapshot of another plan (design Q8): every owner, exhausted ones included, must be the
    /// ordered plan's at the same index, a saved search must be this plan's request at a count the owner accepts, and a
    /// saved cursor must be redeemed the way this plan's stream redeems one.
    /// </summary>
    private static void CheckPlan(
        SortedContinuation resumeFrom,
        ShardStream[] ordered,
        ResourceType type,
        LastUpdatedSort direction,
        int pageSize)
    {
        if (resumeFrom.Type != type || resumeFrom.Direction != direction || resumeFrom.PageSize != pageSize
            || resumeFrom.Streams.Count != ordered.Length)
        {
            throw new ArgumentException("The continuation belongs to another search.", nameof(resumeFrom));
        }

        for (int i = 0; i < ordered.Length; i++)
        {
            ShardStream stream = ordered[i];
            SortedStreamContinuation saved = resumeFrom.Streams[i];
            bool matches = saved.Shard == stream.Shard
                && (saved.Search is not { } search || IsRequestOf(search, stream))
                && (saved.Cursor is not { } cursor || cursor.Redemption == stream.Redemption);
            if (!matches)
            {
                throw new ArgumentException("The continuation belongs to another search.", nameof(resumeFrom));
            }
        }
    }

    /// <summary>
    /// Gets whether <paramref name="search"/> is <paramref name="stream"/>'s request at a count from 1 to the owner's
    /// limit. The stream's first page at an unbounded count is its template clamped to that limit.
    /// </summary>
    private static bool IsRequestOf(ShardSearchRequest search, ShardStream stream)
    {
        ShardSearchRequest widest = stream.FirstPage(int.MaxValue);
        return search.Shard == widest.Shard
            && search.Type == widest.Type
            && search.Method == widest.Method
            && search.Parameters.SequenceEqual(widest.Parameters)
            && search.PageSize >= 1
            && search.PageSize <= widest.PageSize;
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

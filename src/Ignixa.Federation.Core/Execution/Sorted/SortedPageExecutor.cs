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
    /// or more than one resource type.
    /// </exception>
    /// <exception cref="NotSupportedException"><paramref name="resumeFrom"/> is set; resuming is not implemented yet.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> is not defined, <paramref name="pageSize"/> is less than 1 or <paramref name="fetchBudget"/>
    /// is negative.
    /// </exception>
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
        cancellationToken.ThrowIfCancellationRequested();
        if (resumeFrom is not null)
        {
            // Resume replays each owner's saved input and skips through its anchor; until that lands, refuse rather than
            // silently restart the search from the first page.
            throw new NotSupportedException("Resuming a sorted search is not supported yet.");
        }

        SortedStream[] owners = [.. ordered.Select(stream => new SortedStream(stream, stream.FirstPage(pageSize)))];
        var matches = new List<ShardEntry>(pageSize);
        var scheduler = new FetchScheduler(_client, fetchBudget, _maxParallelFetches, cancellationToken);
        await using (scheduler)
        {
            await MergeAsync(owners, direction, pageSize, matches, scheduler);
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
    private static async Task MergeAsync(
        SortedStream[] owners,
        LastUpdatedSort direction,
        int pageSize,
        List<ShardEntry> matches,
        FetchScheduler scheduler)
    {
        // Ordinal rounds: one input per unresolved owner per round, so no owner's leading empties delay another's head.
        SortedStream[] unresolved = owners;
        while (unresolved.Length > 0)
        {
            foreach (SortedStream owner in unresolved)
            {
                await owner.FetchAsync(scheduler);
            }

            unresolved = [.. unresolved.Where(owner => owner.NeedsFetch)];
        }

        var heads = new PriorityQueue<SortedStream, (DateTimeOffset Instant, int Ordinal)>(EmissionOrder(direction));
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
                await winner.FetchAsync(scheduler);
            }

            Enqueue(heads, winner);
        }
    }

    private static void Enqueue(PriorityQueue<SortedStream, (DateTimeOffset Instant, int Ordinal)> heads, SortedStream owner)
    {
        if (owner.Head is { } head)
        {
            heads.Enqueue(owner, (head.LastUpdated!.Value, owner.Ordinal));
        }
    }

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

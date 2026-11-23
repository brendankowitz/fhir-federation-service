using System.Diagnostics;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.References;

/// <summary>Executes the reverse include searches of one emitted search page (design spec §7.6, §7.8, §8).</summary>
public sealed class RevIncludePageExecutor
{
    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly int _maxParallelFetches;
    private readonly Func<ShardSearchRequest, bool> _fits;

    /// <summary>Initializes a new instance of the <see cref="RevIncludePageExecutor"/> class.</summary>
    /// <param name="client">The client that calls shards.</param>
    /// <param name="registry">The registry every executed plan must have been planned against.</param>
    /// <param name="maxParallelFetches">The most shard calls one execution runs at once.</param>
    /// <param name="fits">
    /// Trusted, synchronous and free of side effects: whether a completed request, measured exactly as the transport sends it
    /// to its shard of <paramref name="registry"/>, is within that shard's request ceiling.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/>, <paramref name="registry"/> or <paramref name="fits"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxParallelFetches"/> is less than 1.</exception>
    public RevIncludePageExecutor(IShardClient client, ShardRegistry registry, int maxParallelFetches, Func<ShardSearchRequest, bool> fits)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fits);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelFetches, 1);
        _client = client;
        _registry = registry;
        _maxParallelFetches = maxParallelFetches;
        _fits = fits;
    }

    /// <summary>
    /// Executes the reverse includes of <paramref name="plan"/> for one emitted page. Every authorized reverse include
    /// searches every shard for sources that reference the page's matches; all its requests are compiled and checked
    /// against their shard's request ceiling before any shard is called. The result is complete or the call fails: no
    /// partial includes are ever returned.
    /// </summary>
    /// <param name="plan">A plan from a planner over this executor's registry.</param>
    /// <param name="emittedMatches">The matches the page emitted, all of the plan's type and on registered shards.</param>
    /// <param name="readableTypes">
    /// The types the caller may read. The plan's type must be one; a reverse include whose source type is not is skipped
    /// and reported as <see cref="RevIncludePage.OmittedUnauthorized"/>.
    /// </param>
    /// <param name="includeLimit">The most distinct resources to include.</param>
    /// <param name="remainingFetches">
    /// The shard calls the request may still start. A first page of every authorized reverse include on every shard is
    /// mandatory, so a cursor is followed only while those calls stay reserved.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The includes, and the shard calls they started.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/>, <paramref name="emittedMatches"/> or <paramref name="readableTypes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="plan"/> was planned against another registry; a match is <see langword="null"/>, of another type or
    /// on an unregistered shard; or a count-only plan has matches.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="includeLimit"/> or <paramref name="remainingFetches"/> is negative.</exception>
    /// <exception cref="UnauthorizedAccessException">The caller may not read the plan's type.</exception>
    /// <exception cref="RevIncludeCompilationException">A reverse include cannot be searched within a shard's certification or request ceiling.</exception>
    /// <exception cref="FetchBudgetExhaustedException">The mandatory first pages, or a cursor still needed, exceed <paramref name="remainingFetches"/>.</exception>
    /// <exception cref="ShardFailureException">A needed shard call failed, timed out or broke the shard contract; the lowest shard ordinal of a batch wins.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<RevIncludePage> ExecuteAsync(
        QueryPlan plan,
        IReadOnlyList<ShardEntry> emittedMatches,
        IReadOnlySet<ResourceType> readableTypes,
        int includeLimit,
        int remainingFetches,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(emittedMatches);
        ArgumentNullException.ThrowIfNull(readableTypes);
        if (!ReferenceEquals(plan.Registry, _registry))
        {
            throw new ArgumentException("The plan was not planned against this executor's registry.", nameof(plan));
        }

        foreach (ShardEntry? match in emittedMatches)
        {
            if (match is null || match.Key.Type != plan.Type || !_registry.TryGetById(match.Key.Shard, out _))
            {
                throw new ArgumentException("Every match must be a resource of the plan's type on a registered shard.", nameof(emittedMatches));
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(includeLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingFetches);

        // Generic on purpose: the message must not disclose which type or shard was refused.
        if (!readableTypes.Contains(plan.Type))
        {
            throw new UnauthorizedAccessException("The caller is not authorized for this search.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (plan.PageSize is not { } pageSize)
        {
            return emittedMatches.Count == 0
                ? new RevIncludePage([], false, false, 0)
                : throw new ArgumentException("A count-only plan emits no matches.", nameof(emittedMatches));
        }

        bool omitted = false;
        var authorized = new List<PlannedOccurrence>();
        foreach (PlannedOccurrence occurrence in plan.Occurrences)
        {
            if (occurrence.Kind != OccurrenceKind.RevInclude)
            {
                continue;
            }

            if (readableTypes.Contains(occurrence.Hop!.Path.Source))
            {
                authorized.Add(occurrence);
            }
            else
            {
                omitted = true;
            }
        }

        ResourceKey[] targets = [.. emittedMatches.Select(match => match.Key).Distinct()];
        if (targets.Length == 0 || authorized.Count == 0)
        {
            return new RevIncludePage([], omitted, false, 0);
        }

        if ((long)authorized.Count * _registry.Shards.Count > remainingFetches)
        {
            throw new FetchBudgetExhaustedException(remainingFetches);
        }

        ShardId[] owners = [.. _registry.Shards.Select(shard => shard.Id)];
        var firstPages = new List<IReadOnlyList<FirstPage>>(authorized.Count);
        foreach (PlannedOccurrence occurrence in authorized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            firstPages.Add(
            [
                .. RevIncludeQueryCompiler.Compile(plan, occurrence, targets, owners, pageSize, _fits)
                    .Select(request => new FirstPage(new ShardStream(request, Owner(request.Shard)), request)),
            ]);
        }

        // No speculation: the first-page batches bound the overlap themselves.
        var scheduler = new FetchScheduler(_client, remainingFetches, maxParallelFetches: 1, cancellationToken);
        var traversal = new Traversal(scheduler, emittedMatches, includeLimit, remainingFetches, firstPages.Sum(pages => pages.Count));
        await using (scheduler)
        {
            try
            {
                foreach (IReadOnlyList<FirstPage> occurrence in firstPages)
                {
                    foreach (FirstPage[] batch in occurrence.Chunk(_maxParallelFetches))
                    {
                        await traversal.RunBatchAsync(batch, cancellationToken);
                    }
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // A failure that raced the caller's cancellation, a contract violation included, yields to it.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RevIncludePage(traversal.Includes, omitted, traversal.Truncated, scheduler.Started);
    }

    private ShardDescriptor Owner(ShardId shard) =>
        _registry.TryGetById(shard, out ShardDescriptor? owner)
            ? owner
            : throw new UnreachableException("The compiler addresses only the registry's shards.");

    /// <summary>A compiled first-page request, sent exactly as fitted, and the stream it starts.</summary>
    private sealed record FirstPage(ShardStream Stream, ShardSearchRequest Request);

    /// <summary>
    /// The ordered traversal of one execution: it runs the first-page batches in turn, and collects distinct eligible
    /// includes until one more than the limit would hold is seen. Used by one caller at a time.
    /// </summary>
    private sealed class Traversal(
        FetchScheduler scheduler,
        IReadOnlyList<ShardEntry> emittedMatches,
        int includeLimit,
        int allowance,
        int firstPages)
    {
        private static readonly IReadOnlySet<ShardCursor> NoneRedeemed = new HashSet<ShardCursor>();

        private readonly HashSet<ResourceKey> _emitted = [.. emittedMatches.Select(match => match.Key)];
        private readonly HashSet<(ResourceKey Key, string? VersionId)> _seen = [];
        private readonly List<ShardEntry> _includes = [];
        private int _unstartedFirstPages = firstPages;

        public IReadOnlyList<ShardEntry> Includes => _includes;

        /// <summary>Gets a value indicating whether a distinct eligible resource beyond the limit was seen.</summary>
        public bool Truncated { get; private set; }

        /// <summary>
        /// Starts first pages of <paramref name="batch"/> in order until cancelled, joins all started calls, then validates in
        /// ordinal order before taking any entry. Once truncated, first pages are still searched and validated, as they are
        /// mandatory, but neither taken nor followed.
        /// </summary>
        public async Task RunBatchAsync(FirstPage[] batch, CancellationToken cancellationToken)
        {
            var searches = new List<Task<ShardPage>>(batch.Length);
            foreach (FirstPage first in batch)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                searches.Add(scheduler.SearchAsync(first.Request));
            }

            _unstartedFirstPages -= searches.Count;
            await ((Task)Task.WhenAll(searches)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellationToken.ThrowIfCancellationRequested();

            var pages = new ShardPage[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                pages[i] = await searches[i];
                batch[i].Stream.Validate(pages[i], NoneRedeemed);
            }

            for (int i = 0; i < batch.Length && !Truncated; i++)
            {
                await DrainAsync(batch[i].Stream, pages[i], cancellationToken);
            }
        }

        /// <summary>
        /// Takes a validated first page and follows its cursors until the stream ends or the includes are truncated. Every
        /// redeemed cursor is remembered before its response is validated, so a replayed cursor is a contract violation.
        /// </summary>
        private async Task DrainAsync(ShardStream stream, ShardPage page, CancellationToken cancellationToken)
        {
            var redeemed = new HashSet<ShardCursor>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Take(page);
                if (Truncated || page.Next is not { } next)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // A follow that would leave a mandatory first page unaffordable cannot lead to a result: fail unsent.
                if ((long)scheduler.Started + _unstartedFirstPages + 1 > allowance)
                {
                    throw new FetchBudgetExhaustedException(allowance);
                }

                redeemed.Add(next);
                page = await scheduler.ContinueAsync(next);
                stream.Validate(page, redeemed);
            }
        }

        /// <summary>
        /// Adds each distinct entry by key and version, a page match excepted, while the limit has room. The first copy
        /// wins. A distinct entry with no room left proves truncation.
        /// </summary>
        private void Take(ShardPage page)
        {
            foreach (ShardEntry entry in page.Entries)
            {
                if (_emitted.Contains(entry.Key) || _seen.Contains((entry.Key, entry.VersionId)))
                {
                    continue;
                }

                if (_includes.Count == includeLimit)
                {
                    Truncated = true;
                    return;
                }

                _seen.Add((entry.Key, entry.VersionId));
                _includes.Add(entry);
            }
        }
    }
}

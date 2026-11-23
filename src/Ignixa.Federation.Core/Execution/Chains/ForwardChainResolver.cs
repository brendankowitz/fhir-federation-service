using System.Collections.ObjectModel;
using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution.References;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Chains;

/// <summary>
/// Resolves a plan's distributed forward chains into unsorted primary requests for <see cref="UnsortedPageExecutor"/>
/// (design spec §7.5). Each distributed chain searches its target on every shard for the planned terminal, keeps the
/// distinct physical keys found, and replaces the chain with one reference list per shard, spelled in the forms that shard
/// certified for own and foreign keys; a key with no certified spelling refuses the plan rather than drop out of the list.
/// A passthrough forward chain is sent unchanged; a reverse chain is refused in either mode, after authorization.
/// </summary>
/// <remarks>
/// <para>
/// Before any request is sent the resolver authorizes the searched type and every type a chain searches, in either mode,
/// refuses semantics this stage does not execute, and hands every witness request, exactly as it will be sent, to the fit
/// predicate. Chains then resolve one at a time in ordinal order, so a chain with no witness proves the result empty
/// without searching later chains. Each primary request is measured, as first sent, before any is exposed.
/// </para>
/// <para>
/// The configured limits and the fit predicate are snapshotted at construction. The resolver holds no per-request state, so
/// one instance may resolve concurrent plans; each resolution owns its calls and joins them before it returns or throws.
/// </para>
/// </remarks>
public sealed class ForwardChainResolver
{
    private static readonly ParameterOccurrence IdsOnly = new("_elements", "id");

    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly Func<ShardSearchRequest, bool> _fits;
    private readonly int _maxChainKeys;
    private readonly int _maxParallelBackendCalls;
    private readonly TimeSpan _chainSubquery;

    /// <summary>
    /// Initializes a new instance of the <see cref="ForwardChainResolver"/> class that measures requests against
    /// operator-trusted byte ceilings.
    /// </summary>
    /// <param name="client">The client every witness search is sent through.</param>
    /// <param name="registry">The registry this resolver serves; a plan must have been made against this very instance.</param>
    /// <param name="requestBounds">
    /// One operator-trusted bound per registry shard, and no other; copied. A request fits when its complete initial form
    /// body (POST) or verbatim request-target (GET), with <c>_count</c>, is within the bound for its method.
    /// </param>
    /// <param name="limits">
    /// The limits; <see cref="LimitOptions.MaxChainKeys"/> and <see cref="LimitOptions.MaxParallelBackendCalls"/> are read
    /// once and must be positive.
    /// </param>
    /// <param name="timeouts">
    /// The timeouts; <see cref="TimeoutOptions.ChainSubquery"/> bounds each chain's witness searches and
    /// <see cref="TimeoutOptions.BackendCall"/> each call. Both are read once and must be positive.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestBounds"/> misses a registry shard, names another shard, omits <see cref="ShardRequestBound.MaxPostBodyBytes"/>
    /// for a shard whose profile supports POST search, or carries it for one that does not.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit or timeout is not positive.</exception>
    public ForwardChainResolver(
        IShardClient client,
        ShardRegistry registry,
        IReadOnlyDictionary<ShardId, ShardRequestBound> requestBounds,
        LimitOptions limits,
        TimeoutOptions timeouts)
        : this(client, registry, limits, timeouts, BoundsFit(registry, requestBounds))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForwardChainResolver"/> class that measures requests with the host's
    /// fit predicate (design spec §7.5).
    /// </summary>
    /// <param name="client">The client every witness search is sent through.</param>
    /// <param name="registry">The registry this resolver serves; a plan must have been made against this very instance.</param>
    /// <param name="limits">
    /// The limits; <see cref="LimitOptions.MaxChainKeys"/> and <see cref="LimitOptions.MaxParallelBackendCalls"/> are read
    /// once and must be positive.
    /// </param>
    /// <param name="timeouts">
    /// The timeouts; <see cref="TimeoutOptions.ChainSubquery"/> bounds each chain's witness searches and
    /// <see cref="TimeoutOptions.BackendCall"/> each call. Both are read once and must be positive.
    /// </param>
    /// <param name="fitsRequest">
    /// Whether a request, exactly as first sent (its shard, type, method, ordered verbatim parameters and the page size it
    /// asks for, without <c>_count</c> among the parameters), fits the transport. It is given every witness request before
    /// any is sent and every primary request after the witness searches; a <see langword="false"/> refuses the plan with
    /// <see cref="ForwardChainRefusalReason.RequestTooLarge"/>. It must be pure and safe to call concurrently; an exception
    /// it throws propagates to the caller of <see cref="ResolveAsync"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit or timeout is not positive.</exception>
    public ForwardChainResolver(
        IShardClient client,
        ShardRegistry registry,
        LimitOptions limits,
        TimeoutOptions timeouts,
        Func<ShardSearchRequest, bool> fitsRequest)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeouts);
        ArgumentNullException.ThrowIfNull(fitsRequest);
        _maxChainKeys = limits.MaxChainKeys;
        _maxParallelBackendCalls = limits.MaxParallelBackendCalls;
        _chainSubquery = timeouts.ChainSubquery;
        TimeSpan backendCall = timeouts.BackendCall;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxChainKeys, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxParallelBackendCalls, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_chainSubquery, TimeSpan.Zero, nameof(timeouts));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backendCall, TimeSpan.Zero, nameof(timeouts));

        _fits = fitsRequest;
        _client = new DeadlineShardClient(client, backendCall);
        _registry = registry;
    }

    /// <summary>Resolves <paramref name="plan"/>'s distributed forward chains.</summary>
    /// <param name="plan">A plan the planner made against this resolver's registry instance.</param>
    /// <param name="readableTypes">The resource types the caller may read.</param>
    /// <param name="fetchBudget">The shard calls the request may still start; witness calls are charged against it.</param>
    /// <param name="payloadByteBudget">The decoded resource bytes the request may still read; witness entries are charged against it.</param>
    /// <param name="cancellationToken">The caller's token, which bounds the whole resolution.</param>
    /// <returns>
    /// <see cref="ForwardChainReady"/> with one primary request per shard; <see cref="ForwardChainEmpty"/> when a chain
    /// has no witness on any shard; or <see cref="ForwardChainRefusal"/>. Each carries the budgets left.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> was made against another registry instance.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A budget is negative.</exception>
    /// <exception cref="FetchBudgetExhaustedException">The witness searches need more calls than <paramref name="fetchBudget"/>.</exception>
    /// <exception cref="ShardFailureException">A witness search failed, timed out by its call deadline, or broke the shard contract.</exception>
    /// <exception cref="ChainSubqueryTimeoutException">A chain's witness searches did not finish within their deadline.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled; it wins over any outcome, and is thrown only after every witness
    /// call has ended.
    /// </exception>
    public async Task<ForwardChainResolution> ResolveAsync(
        QueryPlan plan,
        IReadOnlySet<ResourceType> readableTypes,
        int fetchBudget,
        long payloadByteBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readableTypes);
        ArgumentOutOfRangeException.ThrowIfNegative(fetchBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadByteBudget);
        if (!ReferenceEquals(plan.Registry, _registry))
        {
            throw new ArgumentException("The plan was made against another registry instance.", nameof(plan));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ForwardChainResolution resolution = await ResolveCoreAsync(plan, readableTypes, new Allowance(fetchBudget, payloadByteBudget), cancellationToken);

        // Every witness call has ended by now; a caller that cancelled meanwhile receives its cancellation, not an outcome.
        cancellationToken.ThrowIfCancellationRequested();
        return resolution;
    }

    private async Task<ForwardChainResolution> ResolveCoreAsync(
        QueryPlan plan,
        IReadOnlySet<ResourceType> readableTypes,
        Allowance allowance,
        CancellationToken cancellationToken)
    {
        if (Refuse(plan, readableTypes) is { } refused)
        {
            return allowance.Refuse(refused.Reason, refused.Ordinal, null);
        }

        int pageSize = plan.PageSize!.Value; // Refuse rejected a count-only plan.
        // With reverse chains refused, the replaced occurrences are exactly the distributed forward chains. Each chain's
        // witness requests are built once, in registry order, so the requests measured are the very ones drained.
        PlannedOccurrence[] chains = [.. plan.Occurrences.Where(o => o.ReplacementKey is not null)];
        ShardSearchRequest[][] witnessRequests = [.. chains.Select(WitnessRequests)];
        for (int i = 0; i < chains.Length; i++)
        {
            foreach (ShardSearchRequest witness in witnessRequests[i])
            {
                if (!_fits(witness))
                {
                    return allowance.Refuse(ForwardChainRefusalReason.RequestTooLarge, chains[i].Occurrence.Ordinal, witness.Shard);
                }
            }
        }

        var witnesses = new Dictionary<int, IReadOnlyList<ResourceKey>>();
        for (int i = 0; i < chains.Length; i++)
        {
            PlannedOccurrence chain = chains[i];
            Drained drained = await DrainAsync(chain, witnessRequests[i], allowance, cancellationToken);
            int ordinal = chain.Occurrence.Ordinal;
            if (drained.Refusal is { } refusal)
            {
                return allowance.Refuse(refusal.Reason, ordinal, refusal.Shard);
            }

            if (drained.Keys.Count == 0)
            {
                return new ForwardChainEmpty(ordinal, allowance.Fetches, allowance.PayloadBytes);
            }

            witnesses.Add(ordinal, drained.Keys);
        }

        var streams = new List<ShardSearchRequest>(_registry.Shards.Count);
        foreach (ShardDescriptor shard in _registry.Shards)
        {
            if (!ForwardChainPrimary.TryBuild(plan, shard, witnesses, _fits, out ShardSearchRequest? primary, out var refusal))
            {
                return allowance.Refuse(refusal.Reason, refusal.Ordinal, shard.Id);
            }

            streams.Add(primary);
        }

        return new ForwardChainReady(
            streams.AsReadOnly(),
            pageSize,
            new ReadOnlyDictionary<int, IReadOnlyList<ResourceKey>>(witnesses),
            allowance.Fetches,
            allowance.PayloadBytes);
    }

    /// <summary>Finds the first reason to refuse <paramref name="plan"/> before any I/O: authorization, then semantics.</summary>
    private static (ForwardChainRefusalReason Reason, int Ordinal)? Refuse(QueryPlan plan, IReadOnlySet<ResourceType> readableTypes)
    {
        if (!readableTypes.Contains(plan.Type))
        {
            return (ForwardChainRefusalReason.Unauthorized, -1);
        }

        foreach (PlannedOccurrence planned in plan.Occurrences)
        {
            ResourceType? searched = planned switch
            {
                { Kind: OccurrenceKind.ForwardChain, Hop: { } hop } => hop.Path.Target,
                { Kind: OccurrenceKind.ReverseChain, Hop: { } hop } => hop.Path.Source,
                _ => null,
            };
            if (searched is not null && !readableTypes.Contains(searched))
            {
                return (ForwardChainRefusalReason.Unauthorized, planned.Occurrence.Ordinal);
            }
        }

        foreach (PlannedOccurrence planned in plan.Occurrences)
        {
            ForwardChainRefusalReason? unsupported = planned switch
            {
                { Kind: OccurrenceKind.ReverseChain } => ForwardChainRefusalReason.ReverseChain,
                { Kind: OccurrenceKind.Include } => ForwardChainRefusalReason.Include,
                { Kind: OccurrenceKind.RevInclude } => ForwardChainRefusalReason.RevInclude,
                { Kind: OccurrenceKind.Sort } => ForwardChainRefusalReason.Sort,
                _ => null,
            };
            if (unsupported is { } reason)
            {
                return (reason, planned.Occurrence.Ordinal);
            }
        }

        if (plan.CountOnly)
        {
            return (ForwardChainRefusalReason.CountOnly, -1);
        }

        return plan.IncludeTotal ? (ForwardChainRefusalReason.Total, -1) : null;
    }

    /// <summary>
    /// Builds a chain's witness request for every shard in registry order: exactly the planned terminal, asking only for ids,
    /// for the shard's largest page, by POST when the profile supports it and else by GET (design spec §7.5).
    /// </summary>
    private ShardSearchRequest[] WitnessRequests(PlannedOccurrence chain)
    {
        ParameterOccurrence[] parameters = [chain.Hop!.Terminal!, IdsOnly];
        return
        [
            .. _registry.Shards.Select(s => new ShardSearchRequest(
                s.Id,
                chain.Hop.Path.Target,
                parameters,
                s.Profile.MaxCount,
                s.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get)),
        ];
    }

    /// <summary>
    /// Adapts operator-trusted byte ceilings to a fit predicate: the complete initial request with <c>_count</c> must fit
    /// the ceiling for its method, the form body for POST, or the verbatim request-target (the canonical base path,
    /// <c>/Type?</c> and every raw pair, as transport writes it) for GET.
    /// </summary>
    private static Func<ShardSearchRequest, bool> BoundsFit(ShardRegistry registry, IReadOnlyDictionary<ShardId, ShardRequestBound> requestBounds)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(requestBounds);
        if (requestBounds.Count != registry.Shards.Count)
        {
            throw new ArgumentException("Every registry shard, and no other, needs a request bound.", nameof(requestBounds));
        }

        var bounds = new Dictionary<ShardId, (ShardDescriptor Shard, ShardRequestBound Bound)>();
        foreach (ShardDescriptor shard in registry.Shards)
        {
            if (!requestBounds.TryGetValue(shard.Id, out ShardRequestBound? bound) || bound is null)
            {
                throw new ArgumentException("Every registry shard, and no other, needs a request bound.", nameof(requestBounds));
            }

            // A POST-capable shard can be sent either method (see ForwardChainPrimary), so it needs both ceilings; a GET-only
            // shard is only ever sent GET, and a POST ceiling for it would describe a request that is never made.
            if (shard.Profile.SupportsPostSearch != bound.MaxPostBodyBytes.HasValue)
            {
                throw new ArgumentException(
                    $"Shard '{shard.Id}': a request bound carries a POST body ceiling exactly when the profile supports POST search.",
                    nameof(requestBounds));
            }

            bounds.Add(shard.Id, (shard, bound));
        }

        return request =>
        {
            (ShardDescriptor shard, ShardRequestBound bound) = bounds[request.Shard];
            string query = QueryRewriter.Render(request.Parameters);
            query = query.Length == 0 ? $"_count={request.PageSize}" : $"{query}&_count={request.PageSize}";
            if (request.Method == ShardSearchMethod.PostSearch)
            {
                return Encoding.UTF8.GetByteCount(query) <= bound.MaxPostBodyBytes!.Value; // Only POST-capable shards are sent POST.
            }

            // The registry's canonical base is https with no terminal slash, so its path is whatever follows the authority.
            // Nothing is decoded or re-escaped: System.Uri would decode escaped unreserved characters and expand a raw '|'.
            string baseUrl = shard.CanonicalBaseUrl;
            int path = baseUrl.IndexOf('/', Uri.UriSchemeHttps.Length + "://".Length, StringComparison.Ordinal);
            string target = $"{(path < 0 ? string.Empty : baseUrl[path..])}/{request.Type.Value}?{query}";
            return Encoding.UTF8.GetByteCount(target) <= bound.MaxGetTargetBytes;
        };
    }

    /// <summary>
    /// Searches one distributed chain's target on every shard with its measured <paramref name="requests"/>, in registry
    /// order, and drains every page, within the chain deadline, the key cap and the request's budgets.
    /// </summary>
    private async Task<Drained> DrainAsync(
        PlannedOccurrence chain,
        ShardSearchRequest[] requests,
        Allowance allowance,
        CancellationToken cancellationToken)
    {
        ShardStream[] streams = [.. requests.Zip(_registry.Shards, (r, s) => new ShardStream(r, s))];

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_chainSubquery);
        var scheduler = new FetchScheduler(_client, allowance.Fetches, _maxParallelBackendCalls, deadline.Token);
        try
        {
            scheduler.Speculate(streams.Skip(1).Select(s => s.FirstPage(int.MaxValue)));
            var keys = new HashSet<ResourceKey>();
            foreach (ShardStream stream in streams)
            {
                // Every cursor redeemed for this stream stays in the set, so Validate fails a page naming any of them again:
                // that bounds a cursor cycle by its length without a separate cycle detector.
                var redeemed = new HashSet<ShardCursor>();
                (ShardPage page, _) = await scheduler.FirstPageAsync(stream, int.MaxValue);
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    stream.Validate(page, redeemed);
                    if (!allowance.TryCharge(page))
                    {
                        return new Drained([], (ForwardChainRefusalReason.PayloadBudgetExceeded, stream.Shard));
                    }

                    // One key past the cap proves the refusal, so the rest of an oversize page is never held.
                    foreach (ShardEntry entry in page.Entries)
                    {
                        if (keys.Add(entry.Key) && keys.Count > _maxChainKeys)
                        {
                            return new Drained([], (ForwardChainRefusalReason.TooManyKeys, stream.Shard));
                        }
                    }

                    if (page.Next is not { } next)
                    {
                        break;
                    }

                    redeemed.Add(next);
                    page = await scheduler.ContinueAsync(next);
                }
            }

            return new Drained(ForwardChainPrimary.CanonicalOrder(_registry, keys), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new ChainSubqueryTimeoutException(chain.Occurrence.Ordinal, exception);
        }
        finally
        {
            await scheduler.DisposeAsync();
            allowance.Fetches -= scheduler.Started;
        }
    }

    /// <summary>The request's shard calls and payload bytes still available, carried across every chain.</summary>
    private sealed class Allowance(int fetches, long payloadBytes)
    {
        public int Fetches { get; set; } = fetches;

        public long PayloadBytes { get; private set; } = payloadBytes;

        /// <summary>Charges a page's decoded resource bytes; false, charging nothing, when they exceed what is left.</summary>
        public bool TryCharge(ShardPage page)
        {
            long bytes = page.Entries.Sum(e => (long)e.Resource.Length);
            if (bytes > PayloadBytes)
            {
                return false;
            }

            PayloadBytes -= bytes;
            return true;
        }

        public ForwardChainRefusal Refuse(ForwardChainRefusalReason reason, int ordinal, ShardId? shard) =>
            new(reason, ordinal, shard, Fetches, PayloadBytes);
    }

    /// <summary>A drained chain: its distinct witness keys in shard then id order, or why draining stopped.</summary>
    private sealed record Drained(IReadOnlyList<ResourceKey> Keys, (ForwardChainRefusalReason Reason, ShardId Shard)? Refusal);
}

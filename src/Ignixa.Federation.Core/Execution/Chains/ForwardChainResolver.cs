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
/// refuses semantics this stage does not execute, and measures every witness request against its shard's
/// <see cref="ShardRequestBound"/>. Chains then resolve one at a time in ordinal order, so a chain with no witness proves the
/// result empty without searching later chains. Each primary request is measured before it is exposed.
/// </para>
/// <para>
/// The configured limits and request bounds are snapshotted at construction. The resolver holds no per-request state, so
/// one instance may resolve concurrent plans; each resolution owns its calls and joins them before it returns or throws.
/// </para>
/// </remarks>
public sealed class ForwardChainResolver
{
    private static readonly ParameterOccurrence IdsOnly = new("_elements", "id");

    private readonly IShardClient _client;
    private readonly ShardRegistry _registry;
    private readonly Target[] _targets; // In registry order, which orders witness searches and primary streams.
    private readonly Dictionary<ShardId, Target> _byShard;
    private readonly int _maxChainKeys;
    private readonly int _maxParallelBackendCalls;
    private readonly TimeSpan _chainSubquery;

    /// <summary>Initializes a new instance of the <see cref="ForwardChainResolver"/> class.</summary>
    /// <param name="client">The client every witness search is sent through.</param>
    /// <param name="registry">The registry this resolver serves; a plan must have been made against this very instance.</param>
    /// <param name="requestBounds">One operator-trusted bound per registry shard, and no other; copied.</param>
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
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(requestBounds);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeouts);
        _maxChainKeys = limits.MaxChainKeys;
        _maxParallelBackendCalls = limits.MaxParallelBackendCalls;
        _chainSubquery = timeouts.ChainSubquery;
        TimeSpan backendCall = timeouts.BackendCall;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxChainKeys, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_maxParallelBackendCalls, nameof(limits));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_chainSubquery, TimeSpan.Zero, nameof(timeouts));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(backendCall, TimeSpan.Zero, nameof(timeouts));

        if (requestBounds.Count != registry.Shards.Count)
        {
            throw new ArgumentException("Every registry shard, and no other, needs a request bound.", nameof(requestBounds));
        }

        _byShard = [];
        foreach (ShardDescriptor shard in registry.Shards)
        {
            if (!requestBounds.TryGetValue(shard.Id, out ShardRequestBound? bound) || bound is null)
            {
                throw new ArgumentException("Every registry shard, and no other, needs a request bound.", nameof(requestBounds));
            }

            // A POST-capable shard can be sent either method (see Target.PrimaryMethod), so it needs both ceilings; a GET-only
            // shard is only ever sent GET, and a POST ceiling for it would describe a request that is never made.
            if (shard.Profile.SupportsPostSearch != bound.MaxPostBodyBytes.HasValue)
            {
                throw new ArgumentException(
                    $"Shard '{shard.Id}': a request bound carries a POST body ceiling exactly when the profile supports POST search.",
                    nameof(requestBounds));
            }

            _byShard.Add(shard.Id, new Target(shard, bound));
        }

        _targets = [.. registry.Shards.Select(s => _byShard[s.Id])];
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
        // With reverse chains refused, the replaced occurrences are exactly the distributed forward chains.
        PlannedOccurrence[] chains = [.. plan.Occurrences.Where(o => o.ReplacementKey is not null)];
        foreach (PlannedOccurrence chain in chains)
        {
            foreach (Target target in _targets)
            {
                if (!target.Fits(chain.Hop!.Path.Target, Witness(chain), target.Shard.Profile.MaxCount, target.WitnessMethod))
                {
                    return allowance.Refuse(ForwardChainRefusalReason.RequestTooLarge, chain.Occurrence.Ordinal, target.Shard.Id);
                }
            }
        }

        var witnesses = new Dictionary<int, IReadOnlyList<ResourceKey>>();
        foreach (PlannedOccurrence chain in chains)
        {
            Drained drained = await DrainAsync(chain, allowance, cancellationToken);
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

        var streams = new List<ShardSearchRequest>(_targets.Length);
        foreach (Target target in _targets)
        {
            var replacements = new Dictionary<int, string>(chains.Length);
            foreach (PlannedOccurrence chain in chains)
            {
                int ordinal = chain.Occurrence.Ordinal;
                ReferencePathCertification certified = chain.Hop!.Certifications[target.Shard.Id];
                if (!ReferenceOrList.TryRender(witnesses[ordinal], target.Shard.Id, _registry, certified.OwnSideForms, certified.ForeignForms, out string? orList))
                {
                    return allowance.Refuse(ForwardChainRefusalReason.UnspellableReference, ordinal, target.Shard.Id);
                }

                replacements.Add(ordinal, orList);
            }

            IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(replacements);
            ShardSearchMethod method = target.PrimaryMethod(parameters);
            if (!target.Fits(plan.Type, parameters, Math.Min(pageSize, target.Shard.Profile.MaxCount), method))
            {
                return allowance.Refuse(ForwardChainRefusalReason.RequestTooLarge, -1, target.Shard.Id);
            }

            streams.Add(new ShardSearchRequest(target.Shard.Id, plan.Type, parameters, pageSize, method));
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

    /// <summary>Gets a chain's witness parameters: exactly the planned terminal, asking only for ids.</summary>
    private static ParameterOccurrence[] Witness(PlannedOccurrence chain) => [chain.Hop!.Terminal!, IdsOnly];

    /// <summary>
    /// Searches one distributed chain's target on every shard and drains every page, within the chain deadline, the key cap
    /// and the request's budgets.
    /// </summary>
    private async Task<Drained> DrainAsync(PlannedOccurrence chain, Allowance allowance, CancellationToken cancellationToken)
    {
        ResourceType type = chain.Hop!.Path.Target;
        ParameterOccurrence[] witness = Witness(chain);
        ShardStream[] streams =
        [
            .. _targets.Select(t => new ShardStream(
                new ShardSearchRequest(t.Shard.Id, type, witness, t.Shard.Profile.MaxCount, t.WitnessMethod),
                t.Shard)),
        ];

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

            return new Drained(
                [.. keys.OrderBy(k => _byShard[k.Shard].Shard.Ordinal).ThenBy(k => k.Id.Value, StringComparer.Ordinal)],
                null);
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

    /// <summary>A shard and its trusted request bounds.</summary>
    private sealed class Target
    {
        public Target(ShardDescriptor shard, ShardRequestBound bound)
        {
            Shard = shard;
            Bound = bound;
            WitnessMethod = shard.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
        }

        public ShardDescriptor Shard { get; }

        public ShardRequestBound Bound { get; }

        /// <summary>Gets how witness searches are sent: by POST when the profile supports it, else by GET (design spec §7.5).</summary>
        public ShardSearchMethod WitnessMethod { get; }

        /// <summary>
        /// Chooses how the primary search for <paramref name="parameters"/> is sent. A GET-only shard is sent GET. A POST-capable
        /// shard is sent POST unless two pairs are equal after one form decode (key and value, case-sensitive): a POST backend may
        /// collapse them, so the search goes by GET, which keeps every pair. The raw parameters are never altered.
        /// </summary>
        public ShardSearchMethod PrimaryMethod(IReadOnlyList<ParameterOccurrence> parameters)
        {
            if (!Shard.Profile.SupportsPostSearch)
            {
                return ShardSearchMethod.Get;
            }

            SearchQuery form = SearchQuery.ParseForm(QueryRewriter.Render(parameters)).Query
                ?? throw new InvalidOperationException("The completed primary parameters are not valid form pairs.");
            var decoded = new HashSet<(string Key, string Value)>();
            return form.Occurrences.All(o => decoded.Add((o.Key, o.Value))) ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
        }

        /// <summary>
        /// Gets whether the complete initial request for <paramref name="parameters"/> with <c>_count</c> =
        /// <paramref name="count"/> fits the ceiling for <paramref name="method"/>: the form body for POST, or the verbatim
        /// request-target (the canonical base path, <c>/Type?</c> and every raw pair, as transport writes it) for GET.
        /// </summary>
        public bool Fits(ResourceType type, IReadOnlyList<ParameterOccurrence> parameters, int count, ShardSearchMethod method)
        {
            string query = QueryRewriter.Render(parameters);
            query = query.Length == 0 ? $"_count={count}" : $"{query}&_count={count}";
            if (method == ShardSearchMethod.PostSearch)
            {
                return Encoding.UTF8.GetByteCount(query) <= Bound.MaxPostBodyBytes!.Value; // Only POST-capable shards are sent POST.
            }

            // The registry's canonical base is https with no terminal slash, so its path is whatever follows the authority.
            // Nothing is decoded or re-escaped: System.Uri would decode escaped unreserved characters and expand a raw '|'.
            string baseUrl = Shard.CanonicalBaseUrl;
            int path = baseUrl.IndexOf('/', Uri.UriSchemeHttps.Length + "://".Length, StringComparison.Ordinal);
            string target = $"{(path < 0 ? string.Empty : baseUrl[path..])}/{type.Value}?{query}";
            return Encoding.UTF8.GetByteCount(target) <= Bound.MaxGetTargetBytes;
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

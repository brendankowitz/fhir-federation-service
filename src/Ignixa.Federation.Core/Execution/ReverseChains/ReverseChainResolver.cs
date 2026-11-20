using System.Collections.Frozen;
using System.Runtime.ExceptionServices;
using Ignixa.FhirPath.Expressions;
using Ignixa.Federation.Core.Execution.Includes;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>Prepares targeted primary searches for depth-one reverse chains (design spec §7.5).</summary>
/// <remarks>
/// Each distributed clause independently witnesses physical target keys. Their intersection supplies the same
/// comma-separated alternatives to every replaced ordinal on an owner: combining identical lists with AND or OR
/// selects the same ids. No join on source-row identities or backend AND of different id lists is needed.
/// The sub-query requests full resources because a hop's FHIRPath need not depend on a single element.
/// Enforcing the backend's HTTP request-byte limit is an integration prerequisite: profiles do not specify
/// an accepted GET line or POST body size, and this resolver does not split requests.
/// </remarks>
public sealed class ReverseChainResolver
{
    private static readonly TimeSpan MaxSubqueryTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);

    private readonly IShardClient _client;
    private readonly SearchCapabilities _capabilities;
    private readonly ReferenceClassifier _classifier;
    private readonly int _maxChainSourceRows;
    private readonly int _maxChainKeys;
    private readonly int _maxParallelCalls;
    private readonly TimeSpan _subqueryTimeout;

    /// <summary>Initializes a new instance of the <see cref="ReverseChainResolver"/> class.</summary>
    /// <param name="client">The client that searches source shards and follows their continuations.</param>
    /// <param name="capabilities">The current path certifications and the registry used to route references.</param>
    /// <param name="maxChainKeys">The most distinct physical target keys one distributed clause may witness across all shards.</param>
    /// <param name="maxChainSourceRows">The most source entries one distributed clause may scan across all shards and pages.</param>
    /// <param name="maxParallelCalls">The most shard calls one resolution may have in flight at once.</param>
    /// <param name="subqueryTimeout">The deadline for one distributed clause's fan-out, including continuations.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="capabilities"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxChainKeys"/>, <paramref name="maxChainSourceRows"/>, <paramref name="maxParallelCalls"/>
    /// or <paramref name="subqueryTimeout"/> is not positive, or the timeout exceeds the timer maximum of 4,294,967,294 milliseconds.
    /// </exception>
    public ReverseChainResolver(IShardClient client, SearchCapabilities capabilities, int maxChainKeys, int maxChainSourceRows, int maxParallelCalls, TimeSpan subqueryTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainSourceRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelCalls);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(subqueryTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subqueryTimeout, MaxSubqueryTimeout);
        _client = client;
        _capabilities = capabilities;
        _classifier = new ReferenceClassifier(capabilities.Registry);
        _maxChainSourceRows = maxChainSourceRows;
        _maxChainKeys = maxChainKeys;
        _maxParallelCalls = maxParallelCalls;
        _subqueryTimeout = subqueryTimeout;
    }

    /// <summary>Resolves reverse chains without issuing any primary search.</summary>
    /// <param name="plan">The planned search. Only distributed reverse chains may require replacements.</param>
    /// <param name="authorizedTypes">The types the caller may read; both ends of every reverse-chain path must be authorized.</param>
    /// <param name="fetchBudget">
    /// The most backend calls this resolution may start across all clauses and shards, counting each initial search
    /// and each continuation. Zero is sufficient when no distributed clause needs a call.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution. Caller cancellation takes precedence over a shard failure.</param>
    /// <returns>
    /// The prepared primary targets in registry order and the number of sub-query calls spent. Empty targets mean
    /// no shard can match: return an empty result without a primary search.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="authorizedTypes"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fetchBudget"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> requires a distributed forward-chain replacement.</exception>
    /// <exception cref="ReverseChainRefusedException">
    /// <see cref="ReverseChainRefusal.UnauthorizedType"/>: the caller cannot read a source or target type;
    /// <see cref="ReverseChainRefusal.NotCertified"/>: the operation is no longer certified on exactly the planned shards;
    /// <see cref="ReverseChainRefusal.TooCostly"/>: a distributed clause exceeded its source-row or distinct-key limit.
    /// </exception>
    /// <exception cref="FetchBudgetExhaustedException">Another sub-query call would exceed <paramref name="fetchBudget"/>.</exception>
    /// <exception cref="ShardFailureException">A participating shard failed, timed out, or broke its search contract.</exception>
    /// <exception cref="TimeoutException">The sub-query deadline expired without a failure attributable to a shard.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ReverseChainResolution> ResolveAsync(QueryPlan plan, IReadOnlySet<ResourceType> authorizedTypes, int fetchBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorizedTypes);
        ArgumentOutOfRangeException.ThrowIfNegative(fetchBudget);
        cancellationToken.ThrowIfCancellationRequested();
        Admit(plan, authorizedTypes);
        var budget = new CallBudget(fetchBudget);
        var distributed = plan.Occurrences.Where(o => o.Action == OccurrenceAction.Replace).ToArray();
        List<ResourceKey>? candidates = null;
        foreach (PlannedOccurrence occurrence in distributed)
        {
            ReferenceHop hop = occurrence.Hop!;
            var keys = await WitnessesAsync(hop, budget, cancellationToken);
            candidates = candidates is null ? keys : [.. candidates.Intersect(keys)];
            if (candidates.Count == 0)
            {
                return new ReverseChainResolution([], budget.Calls);
            }
        }

        var targets = new List<ReverseChainTarget>(_capabilities.Registry.Shards.Count);
        foreach (ShardDescriptor shard in _capabilities.Registry.Shards)
        {
            IReadOnlyDictionary<int, string> replacements = FrozenDictionary<int, string>.Empty;
            if (candidates is not null)
            {
                string ids = string.Join(',', candidates.Where(k => k.Shard == shard.Id).Select(k => k.Id));
                if (ids.Length == 0)
                {
                    continue;
                }

                replacements = distributed.ToFrozenDictionary(o => o.Occurrence.Ordinal, _ => ids);
            }

            targets.Add(new ReverseChainTarget(plan, shard, replacements));
        }

        return new ReverseChainResolution(targets.AsReadOnly(), budget.Calls);
    }

    private async Task<List<ResourceKey>> WitnessesAsync(ReferenceHop hop, CallBudget budget, CancellationToken caller)
    {
        var expression = IncludeReferenceExtractor.ParseExpression(hop);
        var witnesses = new ReverseChainWitnesses(hop.Path, _maxChainSourceRows, _maxChainKeys);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        stop.CancelAfter(_subqueryTimeout);
        using var gate = new SemaphoreSlim(_maxParallelCalls);
        Exception? firstFailure = null;
        Task all = Task.WhenAll(_capabilities.Registry.Shards.Select(ScanAsync));
        await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        caller.ThrowIfCancellationRequested();
        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Throw(firstFailure);
        }

        if (!all.IsCompletedSuccessfully && stop.IsCancellationRequested)
        {
            // The fan-out missed its deadline, but no slot holder reported a failure. Do not invent a shard alias.
            throw new TimeoutException("The reverse-chain sub-query exceeded its deadline.");
        }

        await all;
        return witnesses.OrderedKeys();

        async Task ScanAsync(ShardDescriptor shard)
        {
            // Cancellation while waiting is not evidence that this shard timed out.
            await gate.WaitAsync(stop.Token);
            try
            {
                await ReadShardAsync(shard, hop, expression, witnesses, budget, stop.Token);
            }
            catch (Exception exception)
            {
                Exception failure = exception switch
                {
                    ShardFailureException or FetchBudgetExhaustedException or ReverseChainRefusedException => exception,
                    OperationCanceledException => new ShardFailureException(shard.Id, ShardFailureReason.Timeout, exception),
                    _ => new ShardFailureException(shard.Id, ShardFailureReason.Failed, exception),
                };
                if (Interlocked.CompareExchange(ref firstFailure, failure, null) is null)
                {
                    // Stop before releasing this slot. A throwing client callback must not mask the original failure.
                    await stop.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task ReadShardAsync(
        ShardDescriptor shard, ReferenceHop hop, Expression expression, ReverseChainWitnesses witnesses, CallBudget budget, CancellationToken token)
    {
        int wanted = (int)Math.Min(int.MaxValue, (long)_maxChainSourceRows + 1);
        var template = new ShardSearchRequest(shard.Id, hop.Path.Source, [hop.Terminal!], wanted, ReverseChainTarget.MethodFor(shard));
        var stream = new ShardStream(template, shard);
        var redeemed = new HashSet<ShardCursor>();
        CursorCycleState cycle = CursorCycleState.Initial;
        ShardCursor? cursor = null;
        long position = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            budget.Reserve();
            ShardPage page = cursor is null
                ? await _client.SearchAsync(stream.FirstPage(wanted), token)
                : await _client.ContinueAsync(cursor, token);
            token.ThrowIfCancellationRequested();
            stream.Validate(page, redeemed);
            witnesses.ChargeRows(page.Entries.Count);
            foreach (ShardEntry entry in page.Entries)
            {
                token.ThrowIfCancellationRequested();
                foreach (string literal in IncludeReferenceExtractor.Extract(entry, expression))
                {
                    if (_classifier.Classify(entry.Key.Shard, literal) is { Kind: ReferenceKind.Relative or ReferenceKind.KnownAbsolute, Target: { } key }
                        && key.Type == hop.Path.Target)
                    {
                        witnesses.Record(key, shard.Ordinal, position++);
                    }
                }
            }

            cursor = page.Next;
            if (cursor is not null)
            {
                cycle = stream.Follow(cycle, cursor);
                redeemed.Add(cursor);
            }
        }
        while (cursor is not null);
    }

    private sealed class CallBudget(int limit)
    {
        private long _reserved;

        public int Calls => checked((int)_reserved);

        public void Reserve()
        {
            if (Interlocked.Increment(ref _reserved) > limit)
            {
                throw new FetchBudgetExhaustedException(limit);
            }
        }
    }

    private void Admit(QueryPlan plan, IReadOnlySet<ResourceType> authorizedTypes)
    {
        foreach (PlannedOccurrence occurrence in plan.Occurrences)
        {
            if (occurrence.Kind != OccurrenceKind.ReverseChain)
            {
                if (occurrence.Action == OccurrenceAction.Replace)
                {
                    throw new ArgumentException("Only distributed reverse-chain replacements are supported.", nameof(plan));
                }

                continue;
            }

            ReferenceHop hop = occurrence.Hop!;
            if (!authorizedTypes.Contains(hop.Path.Source) || !authorizedTypes.Contains(hop.Path.Target))
            {
                throw new ReverseChainRefusedException(hop.Path, ReverseChainRefusal.UnauthorizedType);
            }

            var operation = occurrence.Action == OccurrenceAction.Replace
                ? ReferencePathOperations.DistributedReverse
                : ReferencePathOperations.NativeReverse;
            if (_capabilities.CertifiedPath(hop.Path, operation) is not { } current
                || current.Count != hop.Certifications.Count || !current.Keys.All(hop.Certifications.ContainsKey))
            {
                throw new ReverseChainRefusedException(hop.Path, ReverseChainRefusal.NotCertified);
            }
        }
    }
}

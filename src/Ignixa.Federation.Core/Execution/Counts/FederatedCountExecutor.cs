using System.Collections.Frozen;
using System.Runtime.ExceptionServices;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Counts;

/// <summary>
/// Computes a search's exact <c>Bundle.total</c> by asking every registered shard for its count alone and summing the
/// totals (design spec §7.1, §7.2, §8).
/// </summary>
/// <remarks>
/// The total is exact or the count fails: a shard that fails, times out or answers with anything but a bare total fails
/// it, and nothing is estimated, truncated or partially summed. Resource identities are server-scoped, so equal ids on
/// two shards are two resources and both count. Every refusal happens before the first call, and every started call is
/// cancelled and joined before a failure is thrown. The executor is stateless between calls and safe to share.
/// </remarks>
public sealed class FederatedCountExecutor
{
    private const string CountShapePrefix = "count:";

    private static readonly ParameterOccurrence SummaryCount = new("_summary", "count");

    private readonly IShardClient _client;
    private readonly SearchCapabilities _capabilities;
    private readonly FrozenDictionary<ShardId, CountDialect> _dialects;
    private readonly int _maxParallelCounts;

    /// <summary>Initializes a new instance of the <see cref="FederatedCountExecutor"/> class.</summary>
    /// <param name="client">The client that calls shards.</param>
    /// <param name="capabilities">The admitted capabilities; their registry defines the shards a count covers.</param>
    /// <param name="dialects">Exactly one count dialect per registered shard. Copied.</param>
    /// <param name="maxParallelCounts">The most shard calls one count runs at once.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/>, <paramref name="capabilities"/> or <paramref name="dialects"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="dialects"/> misses a registered shard, names an unregistered one or holds an undefined dialect.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxParallelCounts"/> is less than 1.</exception>
    public FederatedCountExecutor(
        IShardClient client,
        SearchCapabilities capabilities,
        IReadOnlyDictionary<ShardId, CountDialect> dialects,
        int maxParallelCounts)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(dialects);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelCounts, 1);
        IReadOnlyList<ShardDescriptor> shards = capabilities.Registry.Shards;
        if (dialects.Count != shards.Count
            || shards.Any(s => !dialects.TryGetValue(s.Id, out CountDialect dialect) || !Enum.IsDefined(dialect)))
        {
            throw new ArgumentException("Every registered shard, and no other, needs one defined count dialect.", nameof(dialects));
        }

        _client = client;
        _capabilities = capabilities;
        _dialects = dialects.ToFrozenDictionary();
        _maxParallelCounts = maxParallelCounts;
    }

    /// <summary>Counts the plan's matches on every registered shard, when the plan asks for a total.</summary>
    /// <param name="plan">The planned search.</param>
    /// <param name="targets">Exactly one target per registered shard, in any order.</param>
    /// <param name="callBudget">The most shard calls this count may make. Each searched target needs one; a proven-empty one needs none.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>
    /// The exact total, or <see langword="null"/> without any call when the plan neither is count-only nor asks for a
    /// total.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="targets"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="callBudget"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="targets"/> holds <see langword="null"/>, an unregistered shard or a shard twice, or misses a
    /// registered shard; or a target's replacements do not match the occurrences the plan replaces.
    /// </exception>
    /// <exception cref="ExactCountNotCertifiedException">
    /// Not every shard searches the plan's type or certifies its count shape exact; or a chain's hop is not certified by
    /// these capabilities as the plan certified it: the operation the planner required (native or distributed, forward or
    /// reverse) is no longer certified on every shard, the certified shards differ from the plan's, or, for a reference
    /// rewrite, a shard's coverage is not attested for the current registry version or its emitted forms, coverage or
    /// planned count shapes differ from the plan's.
    /// </exception>
    /// <exception cref="FetchBudgetExhaustedException">The count needs more calls than <paramref name="callBudget"/>.</exception>
    /// <exception cref="ShardFailureException">
    /// A shard failed, timed out, or answered with something other than a bare total of at most 2147483647: no total,
    /// any entry, or a <c>next</c> link.
    /// </exception>
    /// <exception cref="TotalOverflowException">The exact total exceeds 2147483647, the largest <c>Bundle.total</c>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<int?> ExecuteAsync(
        QueryPlan plan,
        IReadOnlyList<ShardCountTarget> targets,
        int callBudget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfNegative(callBudget);
        if (!plan.CountOnly && !plan.IncludeTotal)
        {
            return null;
        }

        ShardSearchRequest[] requests = Admit(plan, targets);
        if (requests.Length > callBudget)
        {
            throw new FetchBudgetExhaustedException(callBudget);
        }

        cancellationToken.ThrowIfCancellationRequested();
        long total = await SumAsync(requests, cancellationToken);
        return total > int.MaxValue ? throw new TotalOverflowException() : (int)total;
    }

    /// <summary>Builds every shard's count request, in registry order, or refuses before any call.</summary>
    private ShardSearchRequest[] Admit(QueryPlan plan, IReadOnlyList<ShardCountTarget> targets)
    {
        if (!_capabilities.Searches(plan.Type)
            || !_capabilities.EveryShard(c => c.ExactCountShapes.Contains(plan.CountShape))
            || !plan.Occurrences.All(HopStillCertified))
        {
            throw new ExactCountNotCertifiedException();
        }

        var byShard = new Dictionary<ShardId, ShardCountTarget>(targets.Count);
        foreach (ShardCountTarget target in targets)
        {
            if (target is null)
            {
                throw new ArgumentException("Count targets must not contain null.", nameof(targets));
            }

            if (!_capabilities.Registry.TryGetById(target.Shard, out _))
            {
                throw new ArgumentException($"Count target shard '{target.Shard.Value}' is not registered.", nameof(targets));
            }

            if (!byShard.TryAdd(target.Shard, target))
            {
                throw new ArgumentException($"Shard '{target.Shard.Value}' has more than one count target.", nameof(targets));
            }
        }

        var requests = new List<ShardSearchRequest>(byShard.Count);
        foreach (ShardDescriptor shard in _capabilities.Registry.Shards)
        {
            if (!byShard.TryGetValue(shard.Id, out ShardCountTarget? target))
            {
                throw new ArgumentException($"Shard '{shard.Id.Value}' has no count target.", nameof(targets));
            }

            if (target.Replacements is not { } replacements)
            {
                continue;
            }

            IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(replacements);
            if (_dialects[shard.Id] == CountDialect.CountZeroWithSummaryCount)
            {
                parameters = [.. parameters, SummaryCount];
            }

            requests.Add(new ShardSearchRequest(shard.Id, plan.Type, parameters, 0, target.Method));
        }

        return [.. requests];
    }

    /// <summary>
    /// Whether a chain the shards receive is still certified by the executor's capabilities exactly as the planner proved
    /// it (design spec §7.5). The plan's hop evidence came from the planner's capabilities, which may since have changed,
    /// so it is rechecked here rather than trusted: the operation the planner required must be certified now, on exactly
    /// the shards the plan certified, under <see cref="SearchCapabilities.CertifiedPath"/>'s rules (every current shard
    /// and, for a reference rewrite, coverage attested for the current registry version). A reference rewrite also sends
    /// resolved references in the planned forms and was proven for its count search, so each shard must still attest the
    /// same forms and coverage and every planned count shape. Removed occurrences, such as includes, reach no shard.
    /// </summary>
    private bool HopStillCertified(PlannedOccurrence occurrence)
    {
        if (occurrence is not { Kind: OccurrenceKind.ForwardChain or OccurrenceKind.ReverseChain, Action: not OccurrenceAction.Remove, Hop: { } hop })
        {
            return true;
        }

        // Mirrors the planner: a passthrough hop is kept and joined natively, a distributed one is replaced.
        bool forward = occurrence.Kind == OccurrenceKind.ForwardChain;
        ReferencePathOperations? required = (hop.Mode, occurrence.Action) switch
        {
            (ChainResolutionMode.Passthrough, OccurrenceAction.Keep) => forward ? ReferencePathOperations.NativeForward : ReferencePathOperations.NativeReverse,
            (ChainResolutionMode.Distributed, OccurrenceAction.Replace) => forward ? ReferencePathOperations.DistributedForward : ReferencePathOperations.DistributedReverse,
            _ => null,
        };
        if (required is not { } operation
            || _capabilities.CertifiedPath(hop.Path, operation) is not { } current
            || current.Count != hop.Certifications.Count)
        {
            return false;
        }

        foreach ((ShardId shard, ReferencePathCertification now) in current)
        {
            if (!hop.Certifications.TryGetValue(shard, out ReferencePathCertification? planned)
                || (operation == ReferencePathOperations.DistributedForward && !StillEmitsAsPlanned(planned, now)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="current"/> still proves what a reference rewrite planned under <paramref name="planned"/>
    /// relied on. The planner proved its exact <c>count:</c> shape was among the planned shapes; keeping every planned
    /// count shape carries that proof over without re-deriving the shape.
    /// </summary>
    private static bool StillEmitsAsPlanned(ReferencePathCertification planned, ReferencePathCertification current) =>
        planned.OwnSideForms == current.OwnSideForms
        && planned.ForeignForms == current.ForeignForms
        && string.Equals(planned.CoveredRegistryVersion, current.CoveredRegistryVersion, StringComparison.Ordinal)
        && planned.ForwardSearchShapes
            .Where(shape => shape.StartsWith(CountShapePrefix, StringComparison.Ordinal))
            .All(current.ForwardSearchShapes.Contains);

    /// <summary>Runs every request, at most <see cref="_maxParallelCounts"/> at once, and sums the totals once all have ended.</summary>
    private async Task<long> SumAsync(ShardSearchRequest[] requests, CancellationToken caller)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        using var gate = new SemaphoreSlim(_maxParallelCounts);
        ShardFailureException? failure = null;
        Task? stopping = null;

        Task<long>[] counts = [.. requests.Select(CountAsync)];
        Task all = Task.WhenAll(counts);
        await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _ = all.Exception;
        if (stopping is not null)
        {
            // A client cancellation callback that throws is observed and discarded; it never replaces the outcome.
            await stopping.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ = stopping.Exception;
        }

        // Every call has ended. A caller that gave up gets cancellation; otherwise the first failure is the outcome, and
        // the calls it cancelled are only its consequences.
        caller.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        long total = 0;
        foreach (Task<long> count in counts)
        {
            total = checked(total + await count);
        }

        return total;

        async Task<long> CountAsync(ShardSearchRequest request)
        {
            await gate.WaitAsync(stop.Token);
            try
            {
                ShardPage page;
                try
                {
                    // A slot freed by a failed call must not start another call.
                    stop.Token.ThrowIfCancellationRequested();
                    page = await _client.SearchAsync(request, stop.Token);
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested)
                {
                    caller.ThrowIfCancellationRequested();
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    throw Fail(new ShardFailureException(request.Shard, ShardFailureReason.Timeout, exception));
                }
                catch (Exception exception)
                {
                    throw Fail(new ShardFailureException(request.Shard, ShardFailureReason.Failed, exception));
                }

                return page is { Total: long shardTotal and <= int.MaxValue, Entries.Count: 0, Next: null }
                    ? shardTotal
                    : throw Fail(new ShardFailureException(request.Shard, ShardFailureReason.ContractViolation, null));
            }
            finally
            {
                gate.Release();
            }
        }

        // The first failure stops every other call before its slot is released, so no queued call starts after it.
        ShardFailureException Fail(ShardFailureException exception)
        {
            if (Interlocked.CompareExchange(ref failure, exception, null) is null)
            {
                stopping = stop.CancelAsync();
            }

            return exception;
        }
    }
}

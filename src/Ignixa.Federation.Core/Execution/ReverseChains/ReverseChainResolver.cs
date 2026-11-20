using System.Collections.Frozen;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>Prepares targeted primary searches for depth-one reverse chains (design spec §7.5).</summary>
public sealed class ReverseChainResolver
{
    private readonly SearchCapabilities _capabilities;

    /// <summary>Creates a resolver with positive key, row and concurrency limits and a positive sub-query timeout.</summary>
    public ReverseChainResolver(IShardClient client, SearchCapabilities capabilities, int maxChainKeys, int maxChainSourceRows, int maxParallelCalls, TimeSpan subqueryTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainSourceRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelCalls);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(subqueryTimeout, TimeSpan.Zero);
        _capabilities = capabilities;
    }

    /// <summary>Resolves reverse chains without issuing any primary search.</summary>
    public Task<ReverseChainResolution> ResolveAsync(QueryPlan plan, IReadOnlySet<ResourceType> authorizedTypes, int fetchBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorizedTypes);
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

        var targets = new List<ReverseChainTarget>(_capabilities.Registry.Shards.Count);
        foreach (ShardDescriptor shard in _capabilities.Registry.Shards)
        {
            targets.Add(new ReverseChainTarget(plan, shard, FrozenDictionary<int, string>.Empty));
        }

        return Task.FromResult(new ReverseChainResolution(targets.AsReadOnly(), 0));
    }
}

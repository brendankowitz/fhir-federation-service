using System.Collections.Frozen;
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
    private readonly IShardClient _client;
    private readonly SearchCapabilities _capabilities;
    private readonly ReferenceClassifier _classifier;

    /// <summary>Creates a resolver with positive key, row and concurrency limits and a positive sub-query timeout.</summary>
    public ReverseChainResolver(IShardClient client, SearchCapabilities capabilities, int maxChainKeys, int maxChainSourceRows, int maxParallelCalls, TimeSpan subqueryTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChainSourceRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelCalls);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(subqueryTimeout, TimeSpan.Zero);
        _client = client;
        _capabilities = capabilities;
        _classifier = new ReferenceClassifier(capabilities.Registry);
    }

    /// <summary>Resolves reverse chains without issuing any primary search.</summary>
    public async Task<ReverseChainResolution> ResolveAsync(QueryPlan plan, IReadOnlySet<ResourceType> authorizedTypes, int fetchBudget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(authorizedTypes);
        Admit(plan, authorizedTypes);
        int calls = 0;
        var distributed = plan.Occurrences.Where(o => o.Action == OccurrenceAction.Replace).ToArray();
        List<ResourceKey>? candidates = null;
        foreach (PlannedOccurrence occurrence in distributed)
        {
            ReferenceHop hop = occurrence.Hop!;
            var expression = IncludeReferenceExtractor.ParseExpression(hop);
            var witnesses = new List<ResourceKey>();
            foreach (ShardDescriptor shard in _capabilities.Registry.Shards)
            {
                var request = new ShardSearchRequest(shard.Id, hop.Path.Source, [hop.Terminal!], shard.Profile.MaxCount, ReverseChainTarget.MethodFor(shard));
                ShardPage page = await _client.SearchAsync(request, cancellationToken);
                calls++;
                foreach (ShardEntry entry in page.Entries)
                {
                    foreach (string literal in IncludeReferenceExtractor.Extract(entry, expression))
                    {
                        if (_classifier.Classify(entry.Key.Shard, literal) is { Kind: ReferenceKind.Relative or ReferenceKind.KnownAbsolute, Target: { } key }
                            && key.Type == hop.Path.Target)
                        {
                            witnesses.Add(key);
                        }

                    }
                }
            }

            candidates = candidates is null ? [.. witnesses.Distinct()] : [.. candidates.Intersect(witnesses)];
            if (candidates.Count == 0)
            {
                return new ReverseChainResolution([], calls);
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

        return new ReverseChainResolution(targets.AsReadOnly(), calls);
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

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>One coherent, immutable view of every registered shard's <see cref="ShardCapabilityEntry"/>.</summary>
/// <remarks>
/// A published snapshot never changes; the refresher publishes a new one instead. It belongs to the one registry its
/// refresher was created with, which <see cref="TryCreateSearchCapabilities"/> uses.
/// </remarks>
public sealed class ShardCapabilitySnapshot
{
    private readonly ShardRegistry _registry;
    private readonly ImmutableDictionary<ShardId, ShardCapabilityEntry> _shards;

    internal ShardCapabilitySnapshot(ShardRegistry registry, ImmutableDictionary<ShardId, ShardCapabilityEntry> shards)
    {
        _registry = registry;
        _shards = shards;
    }

    /// <summary>Gets one entry for every shard in the registry, keyed by shard identifier.</summary>
    public IReadOnlyDictionary<ShardId, ShardCapabilityEntry> Shards => _shards;

    /// <summary>Gets whether every registered shard has supplied evidence. This does not indicate readiness or certification.</summary>
    public bool IsComplete => _shards.Values.All(entry => !entry.IsIncomplete);

    /// <summary>Gets whether any shard's latest fetch failed after it had previously supplied evidence.</summary>
    public bool IsDegraded => _shards.Values.Any(entry => entry.IsDegraded);

    /// <summary>
    /// Captures planner input for every shard of this snapshot's registry, pairing each shard's advertisement here with the
    /// caller's proof for exactly that descriptor and evidence instance.
    /// </summary>
    /// <param name="proofs">
    /// Materialized proofs, in any order. A proof binds by reference to a descriptor of this snapshot's registry and to the
    /// evidence instance this snapshot holds for that shard; others are ignored. The caller is accountable for each
    /// certification, which is never inferred from the advertisement, and an empty one is a legal, deliberate proof.
    /// </param>
    /// <param name="capabilities">
    /// The view over every registered shard, citing this snapshot's registry; <see langword="null"/> when there is none.
    /// It never changes, whatever the refresher publishes later.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when a shard has never supplied evidence or has no proof for its current evidence instance.
    /// Never a partial view. This does not indicate readiness. Evidence kept through a failed fetch is the same instance
    /// and keeps its proof; a successful refetch yields a new instance that needs a new proof even if it reads the same.
    /// </returns>
    /// <remarks>Synchronous and pure: it starts no fetch and runs no caller code.</remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="proofs"/> is default, holds <see langword="null"/>, or binds one descriptor and evidence pair twice
    /// (no order decides between them); or an advertisement the proofs match cannot become planner input
    /// (see <see cref="ShardCapabilityBridge.Create"/>).
    /// </exception>
    public bool TryCreateSearchCapabilities(ImmutableArray<ShardCapabilityProof> proofs, [NotNullWhen(true)] out SearchCapabilities? capabilities)
    {
        capabilities = null;
        if (proofs.IsDefault || proofs.Any(proof => proof is null))
        {
            throw new ArgumentException("Proofs are a materialized array of proofs.", nameof(proofs));
        }

        // Reference equality: neither type overrides Equals.
        var seen = new HashSet<(ShardDescriptor, ShardCapabilityEvidence)>();
        if (!proofs.All(proof => seen.Add((proof.Shard, proof.Evidence))))
        {
            throw new ArgumentException("A shard descriptor and evidence pair has one proof.", nameof(proofs));
        }

        var bound = new List<ShardCapabilityProof>(_registry.Shards.Count);
        foreach (ShardDescriptor shard in _registry.Shards)
        {
            if (_shards[shard.Id].Evidence is not { } evidence
                || proofs.SingleOrDefault(p => ReferenceEquals(p.Shard, shard) && ReferenceEquals(p.Evidence, evidence)) is not { } proof)
            {
                return false;
            }

            bound.Add(proof);
        }

        capabilities = new SearchCapabilities(
            _registry,
            bound.Select(proof => ShardCapabilityBridge.Create(proof.Shard, proof.Evidence, proof.Certification)));
        return true;
    }

    internal ShardCapabilitySnapshot With(ShardId shard, ShardCapabilityEntry entry) => new(_registry, _shards.SetItem(shard, entry));
}

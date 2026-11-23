using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>One coherent, immutable view of every registered shard's <see cref="ShardCapabilityEntry"/>.</summary>
/// <remarks>
/// A published snapshot never changes; the refresher publishes a new one instead. It belongs to the one registry its
/// refresher was created with, which <see cref="TryCreateSearchCapabilities"/> uses.
/// </remarks>
public sealed class ShardCapabilitySnapshot
{
    private static readonly ResourceType PatientType = ResourceType.Parse("Patient");

    // Deliberately empty, whatever the caller's proofs say: the narrow projection certifies nothing beyond ordinary search.
    private static readonly SearchCertification NoProof = new(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);

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
        if (!TryBindProofs(proofs, out List<ShardCapabilityProof>? bound))
        {
            return false;
        }

        capabilities = new SearchCapabilities(
            _registry,
            bound.Select(proof => ShardCapabilityBridge.Create(proof.Shard, proof.Evidence, proof.Certification)));
        return true;
    }

    /// <summary>
    /// Captures planner input for <c>Patient</c> alone and only the ordinary search parameters a trusted caller has measured.
    /// </summary>
    /// <param name="proofs">
    /// Materialized proofs bound exactly as for <see cref="TryCreateSearchCapabilities"/>. Only the binding is used: the
    /// optional modifiers, repetition, alternatives, exact counts and reference paths each proof certifies are ignored, and
    /// every shard is treated as if it had supplied an empty <see cref="SearchCertification"/>.
    /// </param>
    /// <param name="requiredParameterCodes">
    /// The mandatory Patient search codes, such as <c>_id</c> and <c>given</c>; compared ordinally, never case-folded, and
    /// each repeated code counts once. Every one must end up in the published Patient parameters.
    /// </param>
    /// <param name="capabilities">
    /// The view citing this snapshot's registry, over Patient alone with no includes or revincludes; <see langword="null"/>
    /// when there is none. It never changes, whatever the refresher publishes later.
    /// </param>
    /// <returns>
    /// <see langword="false"/> when a shard has never supplied evidence, has no proof for its current evidence instance,
    /// advertises no searchable Patient, or when Core does not publish every required code: a code missing on any shard, or
    /// one whose declarations disagree with the R4 meaning. Never a narrower, partially populated view. This does not
    /// indicate readiness.
    /// </returns>
    /// <remarks>
    /// <para>Synchronous and pure, like <see cref="TryCreateSearchCapabilities"/>. Every declaration of a required code, on
    /// the common list and on Patient, is kept in advertised order and duplicates are not merged, so Core resolves any
    /// disagreement.</para>
    /// <para>The registry is reused unchanged. <see cref="SearchCapabilities.SupportsLastUpdatedSort"/> is a registry
    /// profile fact, not an optional search certification; an unsorted-only caller must use profiles that do not
    /// certify stable <c>_lastUpdated</c> order across the registry.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="requiredParameterCodes"/> is default, empty, or contains a blank or <see langword="null"/> value.
    /// Also as for <see cref="TryCreateSearchCapabilities"/>, for the proofs and for a selected declaration the planner
    /// cannot represent.
    /// </exception>
    public bool TryCreatePatientSearchCapabilities(
        ImmutableArray<ShardCapabilityProof> proofs,
        ImmutableArray<string> requiredParameterCodes,
        [NotNullWhen(true)] out SearchCapabilities? capabilities)
    {
        capabilities = null;
        if (requiredParameterCodes.IsDefaultOrEmpty || requiredParameterCodes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The required Patient search codes are a non-empty array of search codes.", nameof(requiredParameterCodes));
        }

        if (!TryBindProofs(proofs, out List<ShardCapabilityProof>? bound))
        {
            return false;
        }

        var required = requiredParameterCodes.ToHashSet(StringComparer.Ordinal);
        var shards = new List<ShardSearchCapability>(bound.Count);
        foreach (ShardCapabilityProof proof in bound)
        {
            if (!proof.Evidence.SearchableResources.TryGetValue(PatientType, out ResourceSearchEvidence? patient))
            {
                return false;
            }

            shards.Add(new ShardSearchCapability(
                proof.Shard.Id,
                [new ResourceSearchSupport(
                    PatientType,
                    proof.Evidence.CommonSearchParameters.Concat(patient.SearchParameters)
                        .Where(parameter => required.Contains(parameter.Code))
                        .Select(ShardCapabilityBridge.MapParameter),
                    [],
                    [])],
                NoProof));
        }

        var view = new SearchCapabilities(_registry, shards);
        if (view.Resources.SingleOrDefault(resource => resource.Type == PatientType) is not { } published
            || !required.All(code => published.Parameters.Any(parameter => parameter.Code == code)))
        {
            return false;
        }

        capabilities = view;
        return true;
    }

    // The one binding both projections share: argument checks, then one proof per shard for its current evidence instance.
    private bool TryBindProofs(ImmutableArray<ShardCapabilityProof> proofs, [NotNullWhen(true)] out List<ShardCapabilityProof>? bound)
    {
        bound = null;
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

        var matched = new List<ShardCapabilityProof>(_registry.Shards.Count);
        foreach (ShardDescriptor shard in _registry.Shards)
        {
            if (_shards[shard.Id].Evidence is not { } evidence
                || proofs.SingleOrDefault(p => ReferenceEquals(p.Shard, shard) && ReferenceEquals(p.Evidence, evidence)) is not { } proof)
            {
                return false;
            }

            matched.Add(proof);
        }

        bound = matched;
        return true;
    }

    internal ShardCapabilitySnapshot With(ShardId shard, ShardCapabilityEntry entry) => new(_registry, _shards.SetItem(shard, entry));
}

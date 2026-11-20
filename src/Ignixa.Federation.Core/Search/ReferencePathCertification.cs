namespace Ignixa.Federation.Core.Search;

/// <summary>A shard's proof for one <see cref="ReferencePath"/>: the operations it performs exactly and, for reference searches, the values it is sent.</summary>
/// <remarks>
/// <para>
/// The forms matter only when the federation searches the shard by reference values, for
/// <see cref="ReferencePathOperations.DistributedForward"/> and <see cref="ReferencePathOperations.RevIncludeSearch"/>.
/// They name the values the federation emits (design spec §7.5), built from the registry: each matched key's registered
/// canonical base or approved link base aliases, never a caller-provided base. They do not say which stored spellings a
/// value matches; <see cref="CoveredRegistryVersion"/> does.
/// </para>
/// <para>
/// Each flagged reference-search operation is proven separately, for the declared forms and for the single generated OR
/// predicate it actually sends, together with the caller's other filters the planner keeps beside it, not merely for its
/// individual search syntax. A forward-chain proof says nothing about revinclude, and the reverse. Proofs come only from
/// configuration; nothing in a request supplies a form, a base or a proof.
/// </para>
/// </remarks>
public sealed record ReferencePathCertification
{
    private const ReferencePathOperations SearchesByReference = ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch;

    private const ReferencePathOperations DefinedOperations = ReferencePathOperations.NativeForward | ReferencePathOperations.NativeReverse
        | ReferencePathOperations.DistributedForward | ReferencePathOperations.DistributedReverse
        | ReferencePathOperations.IncludeExtraction | ReferencePathOperations.RevIncludeSearch;

    private const ReferenceSearchForms DefinedForms = ReferenceSearchForms.Relative | ReferenceSearchForms.CanonicalAbsolute | ReferenceSearchForms.LinkBaseAliases;

    /// <summary>Initializes a new instance of the <see cref="ReferencePathCertification"/> class.</summary>
    /// <param name="path">The path proven.</param>
    /// <param name="operations">The operations proven; at least one, and only defined flags.</param>
    /// <param name="ownSideForms">
    /// The forms emitted for keys on the queried shard itself. Required, and only allowed, when <paramref name="operations"/>
    /// searches by reference.
    /// </param>
    /// <param name="foreignForms">
    /// The forms emitted for keys on other shards. When <paramref name="operations"/> searches by reference they must include
    /// <see cref="ReferenceSearchForms.CanonicalAbsolute"/> and exclude <see cref="ReferenceSearchForms.Relative"/>: another
    /// shard's key is emitted only absolute (design spec §7.5). Otherwise they must be <see cref="ReferenceSearchForms.None"/>.
    /// </param>
    /// <param name="coveredRegistryVersion">
    /// The <see cref="Shards.ShardRegistry.Version"/> the emitted forms were proven against: they match every supported stored
    /// spelling of each key under that registry (every owner's canonical base and every registered link base alias) and no
    /// local or other-owner reference with the same type and id. Required, and only allowed, when
    /// <paramref name="operations"/> searches by reference. This attestation is what lets canonical-only emission stand
    /// while aliases are registered.
    /// </param>
    /// <exception cref="ArgumentException">The operations, forms and coverage are undefined or inconsistent.</exception>
    public ReferencePathCertification(
        ReferencePath path,
        ReferencePathOperations operations,
        ReferenceSearchForms ownSideForms,
        ReferenceSearchForms foreignForms,
        string? coveredRegistryVersion = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (operations == ReferencePathOperations.None || (operations & ~DefinedOperations) != 0)
        {
            throw new ArgumentException("A certification proves at least one operation, and only defined ones.", nameof(operations));
        }

        if ((ownSideForms & ~DefinedForms) != 0 || (foreignForms & ~DefinedForms) != 0)
        {
            throw new ArgumentException("Forms are defined reference spellings.", nameof(ownSideForms));
        }

        if ((operations & SearchesByReference) != 0)
        {
            if (ownSideForms == ReferenceSearchForms.None)
            {
                throw new ArgumentException("Searching by reference needs at least one own-side form.", nameof(ownSideForms));
            }

            if (!foreignForms.HasFlag(ReferenceSearchForms.CanonicalAbsolute) || foreignForms.HasFlag(ReferenceSearchForms.Relative))
            {
                throw new ArgumentException("Foreign keys are emitted in canonical absolute form and never in relative form.", nameof(foreignForms));
            }

            if (coveredRegistryVersion is not { Length: 64 } || !coveredRegistryVersion.All(char.IsAsciiHexDigitLower))
            {
                throw new ArgumentException("Searching by reference needs coverage attested for a registry version.", nameof(coveredRegistryVersion));
            }
        }
        else if (ownSideForms != ReferenceSearchForms.None || foreignForms != ReferenceSearchForms.None || coveredRegistryVersion is not null)
        {
            throw new ArgumentException("Forms and coverage are certified only for operations that search by reference.", nameof(ownSideForms));
        }

        Path = path;
        Operations = operations;
        OwnSideForms = ownSideForms;
        ForeignForms = foreignForms;
        CoveredRegistryVersion = coveredRegistryVersion;
    }

    /// <summary>Gets the path.</summary>
    public ReferencePath Path { get; }

    /// <summary>Gets the proven operations.</summary>
    public ReferencePathOperations Operations { get; }

    /// <summary>Gets the forms emitted for keys on the queried shard itself.</summary>
    public ReferenceSearchForms OwnSideForms { get; }

    /// <summary>Gets the forms emitted for keys on other shards.</summary>
    public ReferenceSearchForms ForeignForms { get; }

    /// <summary>
    /// Gets the registry version whose stored spellings the emitted forms cover, or <see langword="null"/> when no operation
    /// searches by reference. The reference searches are admitted only while it equals the current registry version.
    /// </summary>
    public string? CoveredRegistryVersion { get; }
}

using System.Collections.Frozen;

namespace Ignixa.Federation.Core.Search;

/// <summary>A shard's proof for one <see cref="ReferencePath"/>: the operations it performs exactly and, for reference searches, the values it is sent.</summary>
/// <remarks>
/// <para>
/// The forms matter only when the federation searches the shard by reference values, for
/// <see cref="ReferencePathOperations.DistributedForward"/> and <see cref="ReferencePathOperations.RevIncludeSearch"/>.
/// They name the values the federation emits (design spec §7.5), built from the registry: each matched key's registered
/// canonical base or approved link base aliases, never a caller-provided base. They do not say which stored spellings a
/// value matches; the shape evidence attests to that coverage under <see cref="CoveredRegistryVersion"/>.
/// </para>
/// <para>
/// Each shape proves selection for the entire outbound search, including all permitted nonempty generated key lists:
/// singleton through the supported bound, with own-only, foreign-only, and mixed keys. A proof for one operation or shape
/// does not authorize another. Profiles supply operator-authored evidence for configuration-safe literals (for example
/// a fixed status), never learned from live requests. Unproven combinations are unsupported; broader value classes
/// require their own shape-specific evidence. Nothing in a request supplies a form, a base or a proof.
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
    /// spelling of each key under that registry (relative on its owner, canonical absolute, and registered link base
    /// aliases) and no foreign-base or other-owner reference with the same type and id. Required, and only allowed, when
    /// <paramref name="operations"/> searches by reference. This attestation is what lets canonical-only emission stand
    /// while aliases are registered.
    /// </param>
    /// <param name="forwardSearchShapes">
    /// Allowed primary search shapes for <see cref="ReferencePathOperations.DistributedForward"/>. Start with
    /// <c>Type?</c> for hits or <c>count:Type?</c> for counts. In outbound order, join occurrences with <c>&amp;</c>.
    /// Use <c>@Source.code:Target</c> for a generated reference list and <c>@has:Source.code:Target</c> for a generated
    /// reverse-chain ID list. Encode kept decoded key/value pairs with <see cref="QueryRewriter.Encode"/> and join each
    /// pair with <c>=</c>; retain repeats and sort. For example, <c>Observation?@Observation.subject:Patient&amp;code=1</c>.
    /// Do not include subquery terminals or gateway controls. No entry means no proof; wildcards are not supported.
    /// </param>
    /// <param name="revIncludeSearchShape">
    /// The standalone source search proven for <see cref="ReferencePathOperations.RevIncludeSearch"/>:
    /// <c>Source?@Source.code:Target</c>, without any primary filters or controls. Null means no proof.
    /// </param>
    /// <exception cref="ArgumentException">The operations, forms, coverage or shapes are undefined or inconsistent.</exception>
    public ReferencePathCertification(
        ReferencePath path,
        ReferencePathOperations operations,
        ReferenceSearchForms ownSideForms,
        ReferenceSearchForms foreignForms,
        string? coveredRegistryVersion = null,
        IEnumerable<string>? forwardSearchShapes = null,
        string? revIncludeSearchShape = null)
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

        ForwardSearchShapes = (forwardSearchShapes ?? []).ToFrozenSet(StringComparer.Ordinal);
        if (ForwardSearchShapes.Any(string.IsNullOrWhiteSpace)
            || (ForwardSearchShapes.Count != 0 && !operations.HasFlag(ReferencePathOperations.DistributedForward)))
        {
            throw new ArgumentException("Forward shapes must be nonblank and require distributed forward search.", nameof(forwardSearchShapes));
        }

        if (revIncludeSearchShape is not null
            && (!operations.HasFlag(ReferencePathOperations.RevIncludeSearch) || revIncludeSearchShape != $"{path.Source.Value}?@{path}"))
        {
            throw new ArgumentException("Revinclude evidence must describe the standalone source search for this path.", nameof(revIncludeSearchShape));
        }

        Path = path;
        Operations = operations;
        OwnSideForms = ownSideForms;
        ForeignForms = foreignForms;
        CoveredRegistryVersion = coveredRegistryVersion;
        RevIncludeSearchShape = revIncludeSearchShape;
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

    /// <summary>Gets the complete primary search shapes proven for a distributed forward rewrite.</summary>
    internal IReadOnlySet<string> ForwardSearchShapes { get; }

    /// <summary>Gets the proven standalone revinclude search shape, or <see langword="null"/> without that proof.</summary>
    internal string? RevIncludeSearchShape { get; }

    /// <summary>Returns the type name without proof material.</summary>
    /// <returns>The certification type name.</returns>
    public override string ToString() => nameof(ReferencePathCertification);
}

namespace Ignixa.Federation.Core.Search;

/// <summary>A shard's proof for one <see cref="ReferencePath"/>: the operations it performs exactly and the reference spellings it matches.</summary>
/// <remarks>
/// The forms matter only when the federation searches the shard by reference values, for
/// <see cref="ReferencePathOperations.DistributedForward"/> and <see cref="ReferencePathOperations.RevIncludeSearch"/>.
/// The executor builds the actual values from the registry: each matched key's registered base or approved aliases,
/// never a caller-provided base.
/// </remarks>
public sealed record ReferencePathCertification
{
    private const ReferencePathOperations SearchesByReference = ReferencePathOperations.DistributedForward | ReferencePathOperations.RevIncludeSearch;

    /// <summary>Initializes a new instance of the <see cref="ReferencePathCertification"/> class.</summary>
    /// <param name="path">The path proven.</param>
    /// <param name="operations">The operations proven; at least one.</param>
    /// <param name="ownSideForms">The spellings that match references to the shard's own resources. Required, and only allowed, when <paramref name="operations"/> searches by reference.</param>
    /// <param name="foreignForms">
    /// The spellings that match references to other shards' resources. When <paramref name="operations"/> searches by reference
    /// they must include <see cref="ReferenceSearchForms.CanonicalAbsolute"/> and exclude <see cref="ReferenceSearchForms.Relative"/>;
    /// otherwise they must be <see cref="ReferenceSearchForms.None"/>.
    /// </param>
    /// <exception cref="ArgumentException">The operations and forms are inconsistent.</exception>
    public ReferencePathCertification(
        ReferencePath path,
        ReferencePathOperations operations,
        ReferenceSearchForms ownSideForms,
        ReferenceSearchForms foreignForms)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (operations == ReferencePathOperations.None)
        {
            throw new ArgumentException("A certification proves at least one operation.", nameof(operations));
        }

        if ((operations & SearchesByReference) != 0)
        {
            if (ownSideForms == ReferenceSearchForms.None)
            {
                throw new ArgumentException("Searching by reference needs at least one own-side form.", nameof(ownSideForms));
            }

            if (!foreignForms.HasFlag(ReferenceSearchForms.CanonicalAbsolute) || foreignForms.HasFlag(ReferenceSearchForms.Relative))
            {
                throw new ArgumentException("Foreign references are matched by canonical absolute form and never by relative form.", nameof(foreignForms));
            }
        }
        else if (ownSideForms != ReferenceSearchForms.None || foreignForms != ReferenceSearchForms.None)
        {
            throw new ArgumentException("Forms are certified only for operations that search by reference.", nameof(ownSideForms));
        }

        Path = path;
        Operations = operations;
        OwnSideForms = ownSideForms;
        ForeignForms = foreignForms;
    }

    /// <summary>Gets the path.</summary>
    public ReferencePath Path { get; }

    /// <summary>Gets the proven operations.</summary>
    public ReferencePathOperations Operations { get; }

    /// <summary>Gets the spellings that match references to the shard's own resources.</summary>
    public ReferenceSearchForms OwnSideForms { get; }

    /// <summary>Gets the spellings that match references to other shards' resources.</summary>
    public ReferenceSearchForms ForeignForms { get; }
}

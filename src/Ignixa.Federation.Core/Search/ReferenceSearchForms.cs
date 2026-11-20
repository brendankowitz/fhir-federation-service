namespace Ignixa.Federation.Core.Search;

/// <summary>
/// The reference values the federation emits when it searches a shard by reference: spellings built from the registry,
/// not the stored spellings they match. Which stored spellings a form matches, and which it must not, is attested by
/// <see cref="ReferencePathCertification.CoveredRegistryVersion"/>, never inferred from the form.
/// </summary>
[Flags]
public enum ReferenceSearchForms
{
    /// <summary>No spelling.</summary>
    None = 0,

    /// <summary>Relative, such as <c>Patient/1</c>. Emitted only for keys on the queried shard itself.</summary>
    Relative = 1,

    /// <summary>Absolute under the owning shard's registered canonical base URL.</summary>
    CanonicalAbsolute = 2,

    /// <summary>Absolute under each of the owning shard's registered link base aliases.</summary>
    LinkBaseAliases = 4,
}

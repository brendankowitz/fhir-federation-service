namespace Ignixa.Federation.Core.Search;

/// <summary>Reference spellings a shard's reference search is proven to match.</summary>
[Flags]
public enum ReferenceSearchForms
{
    /// <summary>No spelling.</summary>
    None = 0,

    /// <summary>Relative, such as <c>Patient/1</c>. Names a resource on the shard itself, so it never matches a foreign one.</summary>
    Relative = 1,

    /// <summary>Absolute under the owning shard's registered canonical base URL.</summary>
    CanonicalAbsolute = 2,

    /// <summary>Absolute under one of the owning shard's registered link base aliases.</summary>
    LinkBaseAliases = 4,
}

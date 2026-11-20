namespace Ignixa.Federation.Core.Search;

/// <summary>The reference operations a shard is proven to perform exactly on one <see cref="ReferencePath"/>.</summary>
/// <remarks>A reference parameter that merely compiles proves none of these.</remarks>
[Flags]
public enum ReferencePathOperations
{
    /// <summary>No operation is proven.</summary>
    None = 0,

    /// <summary>The shard answers the forward chain itself (Passthrough mode).</summary>
    NativeForward = 1,

    /// <summary>The shard answers the reverse chain (<c>_has</c>) itself (Passthrough mode).</summary>
    NativeReverse = 2,

    /// <summary>The shard answers <c>code=reference,…</c> for target references found on any shard (Distributed forward chain).</summary>
    DistributedForward = 4,

    /// <summary>The shard's source resources yield their references for the federation to join (Distributed reverse chain).</summary>
    DistributedReverse = 8,

    /// <summary>References on the path can be read from the shard's resources to fetch <c>_include</c> targets.</summary>
    IncludeExtraction = 16,

    /// <summary>The shard answers <c>code=reference,…</c> for matches found on any shard to fetch <c>_revinclude</c> sources.</summary>
    RevIncludeSearch = 32,
}

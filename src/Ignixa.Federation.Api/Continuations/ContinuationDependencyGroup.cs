namespace Ignixa.Federation.Api.Continuations;

/// <summary>Which namespace a continuation dependency key belongs to (design spec §7.4, invalidation).</summary>
internal enum ContinuationDependencyGroup
{
    /// <summary>A shard that one of the token's streams reads; the key is the shard's registry key.</summary>
    Shard = 1,

    /// <summary>A search parameter definition the query resolved; the key identifies the definition.</summary>
    Definition = 2,
}

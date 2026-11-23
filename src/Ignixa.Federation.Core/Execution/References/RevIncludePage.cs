using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.References;

/// <summary>The reverse includes of one emitted search page (design spec §7.6), or why some were left out.</summary>
public sealed class RevIncludePage
{
    internal RevIncludePage(IReadOnlyList<ShardEntry> includes, bool omittedUnauthorized, bool truncated, int fetches)
    {
        Includes = includes;
        OmittedUnauthorized = omittedUnauthorized;
        Truncated = truncated;
        Fetches = fetches;
    }

    /// <summary>
    /// Gets the read-only included resources, each distinct by key and version and none a page match, in occurrence, then source
    /// shard ordinal, then backend order. At most the include limit; the first copy returned wins.
    /// </summary>
    public IReadOnlyList<ShardEntry> Includes { get; }

    /// <summary>Gets a value indicating whether a reverse include was skipped because the caller may not read its source type.</summary>
    public bool OmittedUnauthorized { get; }

    /// <summary>
    /// Gets a value indicating whether at least one further distinct eligible resource exists beyond <see cref="Includes"/>:
    /// set only once one was seen, never because the limit was merely reached.
    /// </summary>
    public bool Truncated { get; }

    /// <summary>Gets the shard calls that started for these includes. The caller subtracts them from the request's allowance.</summary>
    public int Fetches { get; }
}

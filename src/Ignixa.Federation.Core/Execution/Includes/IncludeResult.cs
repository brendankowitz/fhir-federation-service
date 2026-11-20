using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.Includes;

/// <summary>The resources a page's forward includes added, what to report about them, and the shard calls they cost.</summary>
public sealed class IncludeResult
{
    internal IncludeResult(IReadOnlyList<ShardEntry> includes, IReadOnlyList<IncludeOutcome> outcomes, int fetches)
    {
        Includes = includes;
        Outcomes = outcomes;
        Fetches = fetches;
    }

    /// <summary>
    /// Gets the included resources, at most the per-page include limit, in emission order: target groups in the order their
    /// first reference was found, each in backend order. None is a match of the page (same shard, type and id, whatever
    /// its version), and none repeats another include of the same version.
    /// </summary>
    public IReadOnlyList<ShardEntry> Includes { get; }

    /// <summary>Gets one outcome per distinct kind and path, unauthorized types first.</summary>
    public IReadOnlyList<IncludeOutcome> Outcomes { get; }

    /// <summary>Gets the shard calls the includes started, each initial search and each <c>next</c> follow.</summary>
    public int Fetches { get; }
}

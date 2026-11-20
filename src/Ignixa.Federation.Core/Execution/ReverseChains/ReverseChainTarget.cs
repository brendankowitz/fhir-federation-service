using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>One owning shard's prepared primary search, also usable for exact counting.</summary>
/// <remarks>
/// Microsoft FHIR Server can collapse identical key/value occurrences in a form POST response's self link,
/// so the echo cannot be verified. GET is verified to preserve those occurrences. Use <see cref="Method"/>
/// for both the primary search and its exact count; neither drops a repeated predicate.
/// Checking that the URL or request body fits the backend's byte limit remains an integration prerequisite.
/// </remarks>
public sealed class ReverseChainTarget
{
    private readonly ResourceType _type;

    internal ReverseChainTarget(QueryPlan plan, ShardDescriptor descriptor, IReadOnlyDictionary<int, string> replacements)
    {
        _type = plan.Type;
        Shard = descriptor.Id;
        Replacements = replacements;
        Parameters = plan.BuildBackendParameters(replacements);
        Method = Parameters.Distinct().Count() == Parameters.Count ? MethodFor(descriptor) : ShardSearchMethod.Get;
    }

    /// <summary>Gets the shard to search.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the search method that preserves the prepared occurrences on this shard.</summary>
    public ShardSearchMethod Method { get; }

    /// <summary>Gets decoded <c>_id</c> alternatives by original occurrence ordinal.</summary>
    public IReadOnlyDictionary<int, string> Replacements { get; }

    /// <summary>Gets ordered, encoded predicates and sort, without pagination controls.</summary>
    public IReadOnlyList<ParameterOccurrence> Parameters { get; }

    /// <summary>Builds the shard's primary request with the given page size.</summary>
    public ShardSearchRequest Request(int pageSize) => new(Shard, _type, Parameters, pageSize, Method);

    internal static ShardSearchMethod MethodFor(ShardDescriptor descriptor) =>
        descriptor.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;
}

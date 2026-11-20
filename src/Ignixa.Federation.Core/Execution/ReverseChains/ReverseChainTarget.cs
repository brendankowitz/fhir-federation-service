using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Execution.ReverseChains;

/// <summary>One owning shard's prepared primary search, also usable for exact counting.</summary>
public sealed class ReverseChainTarget
{
    private readonly ResourceType _type;

    internal ReverseChainTarget(QueryPlan plan, ShardDescriptor descriptor, IReadOnlyDictionary<int, string> replacements)
    {
        _type = plan.Type;
        Shard = descriptor.Id;
        Method = MethodFor(descriptor);
        Replacements = replacements;
        Parameters = plan.BuildBackendParameters(replacements);
    }

    /// <summary>Gets the shard to search.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the search method selected by the shard's profile.</summary>
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

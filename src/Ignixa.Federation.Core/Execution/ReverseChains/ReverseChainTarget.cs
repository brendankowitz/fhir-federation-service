using System.Net;
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
        Replacements = replacements;
        Parameters = plan.BuildBackendParameters(replacements);
        Method = descriptor.Profile.SupportsPostSearch && !HasFormDuplicates(Parameters)
            ? ShardSearchMethod.PostSearch
            : ShardSearchMethod.Get;
    }

    /// <summary>Gets the shard to search.</summary>
    public ShardId Shard { get; }

    /// <summary>Gets the search method that preserves the prepared occurrences on this shard.</summary>
    /// <remarks>
    /// Choose the verb before sending and use it for both the primary search and its count target. Microsoft FHIR
    /// Server can collapse form-decoded duplicate pairs in a POST self link, so its echo cannot be verified.
    /// GET preserves those occurrences without changing their wire spelling. A cursor stays bound to its start
    /// method; a self-link mismatch is not retried. When the resolver generated the predicates, it measures the
    /// request in this method before returning the target and refuses one that does not fit; GET is not assumed
    /// to fit an arbitrary URL.
    /// </remarks>
    public ShardSearchMethod Method { get; }

    /// <summary>Gets decoded <c>_id</c> alternatives by original occurrence ordinal.</summary>
    public IReadOnlyDictionary<int, string> Replacements { get; }

    /// <summary>Gets ordered, encoded predicates and sort, without pagination controls.</summary>
    public IReadOnlyList<ParameterOccurrence> Parameters { get; }

    /// <summary>Builds the shard's primary request with the given page size.</summary>
    public ShardSearchRequest Request(int pageSize) => new(Shard, _type, Parameters, pageSize, Method);

    internal static ShardSearchMethod MethodFor(ShardDescriptor descriptor) =>
        descriptor.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get;

    private static bool HasFormDuplicates(IReadOnlyList<ParameterOccurrence> parameters)
    {
        var seen = new HashSet<(string Key, string Value)>();
        return parameters.Any(p => !seen.Add((WebUtility.UrlDecode(p.Key), WebUtility.UrlDecode(p.Value))));
    }
}

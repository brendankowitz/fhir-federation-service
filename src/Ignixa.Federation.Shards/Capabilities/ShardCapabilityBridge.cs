using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>Converts one shard's advertised R4 capabilities to planner input (design spec §9).</summary>
public static class ShardCapabilityBridge
{
    /// <summary>Combines one registered shard's advertisement with separately supplied search proofs.</summary>
    /// <param name="shard">The registered shard.</param>
    /// <param name="evidence">The advertisement received from that shard.</param>
    /// <param name="certification">Trusted proofs for the same shard. No proofs are inferred from the advertisement or profile.</param>
    /// <returns>Planner input with the registered identity, exact advertised declarations, and the supplied proofs.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The advertisement has no searchable resources or a code the planner cannot represent.</exception>
    public static ShardSearchCapability Create(ShardDescriptor shard, ShardCapabilityEvidence evidence, SearchCertification certification)
    {
        ArgumentNullException.ThrowIfNull(shard);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(certification);

        SearchParameterSupport[] common = [.. evidence.CommonSearchParameters.Select(MapParameter)];
        return new ShardSearchCapability(
            shard.Id,
            evidence.SearchableResources.Values.Select(resource => new ResourceSearchSupport(
                resource.Type,
                common.Concat(resource.SearchParameters.Select(MapParameter)),
                resource.Includes.Select(value => value.Value),
                resource.RevIncludes.Select(value => value.Value))),
            certification);
    }

    // Keep common and resource declarations even when their codes collide; Core owns admission.
    private static SearchParameterSupport MapParameter(SearchParameterEvidence parameter) => new(
        parameter.Code,
        parameter.Type switch
        {
            SearchParameterType.Number => SearchParamType.Number,
            SearchParameterType.Date => SearchParamType.Date,
            SearchParameterType.String => SearchParamType.String,
            SearchParameterType.Token => SearchParamType.Token,
            SearchParameterType.Reference => SearchParamType.Reference,
            SearchParameterType.Composite => SearchParamType.Composite,
            SearchParameterType.Quantity => SearchParamType.Quantity,
            SearchParameterType.Uri => SearchParamType.Uri,
            SearchParameterType.Special => SearchParamType.Special,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter), "The advertised search parameter type is not an R4 type."),
        },
        parameter.Definition);
}

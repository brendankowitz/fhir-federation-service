using System.Collections.Immutable;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// What one shard's R4 CapabilityStatement advertises (design spec §9). Advertised is not certified: nothing here says the
/// shard behaves as it claims, and nothing is intersected with another shard or a support matrix.
/// </summary>
/// <remarks>Immutable; produced by <see cref="CapabilityStatementReader"/>.</remarks>
public sealed class ShardCapabilityEvidence
{
    internal ShardCapabilityEvidence(
        ImmutableDictionary<ResourceType, ResourceSearchEvidence> searchableResources,
        ImmutableArray<SearchParameterEvidence> commonSearchParameters,
        SoftwareEvidence? software)
    {
        SearchableResources = searchableResources;
        CommonSearchParameters = commonSearchParameters;
        Software = software;
    }

    private ShardCapabilityEvidence(ShardCapabilityEvidence source, ShardDescriptor transportedFrom)
    {
        // The collections are immutable, so the copy shares them.
        SearchableResources = source.SearchableResources;
        CommonSearchParameters = source.CommonSearchParameters;
        Software = source.Software;
        TransportedFrom = transportedFrom;
    }

    /// <summary>
    /// Gets the descriptor whose metadata exchange produced this instance, or <see langword="null"/> when it was read from
    /// bytes without one. Only the shard transport sets it, so a parsed statement cannot claim a shard.
    /// </summary>
    internal ShardDescriptor? TransportedFrom { get; }

    /// <summary>Returns a copy of this advertisement, field for field, marked as fetched from <paramref name="shard"/>.</summary>
    internal ShardCapabilityEvidence FetchedFrom(ShardDescriptor shard) => new(this, shard);

    /// <summary>
    /// Gets the resource types the server-mode statement advertises the <c>search-type</c> interaction for. Empty when the
    /// statement has no server evidence.
    /// </summary>
    public IReadOnlyDictionary<ResourceType, ResourceSearchEvidence> SearchableResources { get; }

    /// <summary>
    /// Gets the server's common search parameters (<c>rest.searchParam</c>), one per code, in declaration order. They are
    /// not merged into any resource's parameters.
    /// </summary>
    public IReadOnlyList<SearchParameterEvidence> CommonSearchParameters { get; }

    /// <summary>Gets the reported software, or <see langword="null"/> when the statement reported neither name nor version.</summary>
    public SoftwareEvidence? Software { get; }
}

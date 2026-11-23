using System.Collections.Immutable;
using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// What a shard's server-mode CapabilityStatement says about searching one resource type it advertises
/// <c>search-type</c> for. Parameters declared on the resource are kept apart from the common ones.
/// </summary>
/// <remarks>Only the reader creates instances. This is advertisement, not certification.</remarks>
public sealed class ResourceSearchEvidence
{
    internal ResourceSearchEvidence(
        ResourceType type,
        ImmutableArray<SearchParameterEvidence> searchParameters,
        ImmutableArray<SearchInclusion> includes,
        ImmutableArray<SearchInclusion> revIncludes,
        bool advertisesRead)
    {
        Type = type;
        SearchParameters = searchParameters;
        Includes = includes;
        RevIncludes = revIncludes;
        AdvertisesRead = advertisesRead;
    }

    /// <summary>
    /// Gets whether this server-mode resource also advertises an unmodified <c>read</c> interaction. A <c>read</c> carrying a
    /// <c>modifierExtension</c>, <c>vread</c>, and an interaction of another resource or mode do not count. Advertised only.
    /// </summary>
    internal bool AdvertisesRead { get; }

    /// <summary>Gets the resource type.</summary>
    public ResourceType Type { get; }

    /// <summary>Gets the parameters declared on this resource, one per code, in declaration order.</summary>
    public IReadOnlyList<SearchParameterEvidence> SearchParameters { get; }

    /// <summary>Gets the explicit <c>searchInclude</c> values, distinct, in declaration order. Wildcards are dropped.</summary>
    public IReadOnlyList<SearchInclusion> Includes { get; }

    /// <summary>Gets the explicit <c>searchRevInclude</c> values, distinct, in declaration order. Wildcards are dropped.</summary>
    public IReadOnlyList<SearchInclusion> RevIncludes { get; }
}

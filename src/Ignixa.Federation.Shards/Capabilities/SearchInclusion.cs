using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Shards.Capabilities;

/// <summary>
/// One explicit, finite <c>searchInclude</c> or <c>searchRevInclude</c> value, such as <c>Observation:subject</c> or
/// <c>Observation:subject:Patient</c>. Whether it is an include or a revinclude is the list it appears in.
/// </summary>
/// <remarks>Only the reader creates instances. Wildcards are never represented.</remarks>
public sealed record SearchInclusion
{
    internal SearchInclusion(string value, ResourceType sourceType, string parameter, ResourceType? targetType)
    {
        Value = value;
        SourceType = sourceType;
        Parameter = parameter;
        TargetType = targetType;
    }

    /// <summary>Gets the value exactly as the shard reported it.</summary>
    public string Value { get; }

    /// <summary>Gets the resource type that holds the search parameter.</summary>
    public ResourceType SourceType { get; }

    /// <summary>Gets the search parameter code, with its exact spelling and case.</summary>
    public string Parameter { get; }

    /// <summary>Gets the target type when the value names one, otherwise <see langword="null"/>.</summary>
    public ResourceType? TargetType { get; }
}

using Ignixa.Federation.Core.Identity;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// The result of classifying one <c>Reference.reference</c> literal (design spec §7.7). Only
/// <see cref="ReferenceKind.Relative"/> and <see cref="ReferenceKind.KnownAbsolute"/> carry a target, so a consumer
/// cannot route a reference the federation must ignore.
/// </summary>
public sealed record ReferenceClassification
{
    private ReferenceClassification(ReferenceKind kind, string? literal, ResourceKey? target, string? version)
    {
        Kind = kind;
        Literal = literal;
        Target = target;
        Version = version;
    }

    /// <summary>Gets how the reference is treated.</summary>
    public ReferenceKind Kind { get; }

    /// <summary>Gets the reference text exactly as it was given, or <see langword="null"/> when there was none.</summary>
    public string? Literal { get; }

    /// <summary>
    /// Gets the one resource the reference names, as a shard-scoped identity: set for <see cref="ReferenceKind.Relative"/>
    /// and <see cref="ReferenceKind.KnownAbsolute"/>, otherwise <see langword="null"/>. A type and id never name a
    /// resource without the shard.
    /// </summary>
    public ResourceKey? Target { get; }

    /// <summary>
    /// Gets the version of a versioned reference (<c>Type/id/_history/version</c>), or <see langword="null"/> when the
    /// reference is unversioned or has no <see cref="Target"/>. Case-sensitive, with the shape of a logical id.
    /// </summary>
    public string? Version { get; }

    internal static ReferenceClassification Resolved(ReferenceKind kind, string literal, ResourceKey target, string? version) =>
        new(kind, literal, target, version);

    internal static ReferenceClassification Unrouted(ReferenceKind kind, string? literal) => new(kind, literal, null, null);
}

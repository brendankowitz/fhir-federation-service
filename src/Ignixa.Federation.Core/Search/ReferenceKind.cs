namespace Ignixa.Federation.Core.Search;

/// <summary>How a <c>Reference.reference</c> literal is treated by the federation (design spec §7.7).</summary>
public enum ReferenceKind
{
    /// <summary>
    /// A relative <c>Type/id</c> or <c>Type/id/_history/version</c>: it names a resource on the containing resource's shard.
    /// </summary>
    Relative,

    /// <summary>
    /// An absolute URL under a registered base URL or link base alias: it names a resource on the shard registered for
    /// that base, whatever shard contains the reference.
    /// </summary>
    KnownAbsolute,

    /// <summary>An absolute reference that no registered base covers. The federation never resolves it.</summary>
    ForeignAbsolute,

    /// <summary>A <c>#id</c> reference to a contained resource. Never routed.</summary>
    Contained,

    /// <summary>The reference has no literal (identifier-only or display-only). Never routed.</summary>
    IdentifierOnly,

    /// <summary>
    /// Malformed, ambiguous or not a resource URL. Never routed: a form that URI normalisation could turn into a local
    /// reference is rejected rather than guessed at.
    /// </summary>
    Invalid,
}

using System.Diagnostics.CodeAnalysis;

namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// One validated shard of a <see cref="ShardRegistry"/> (design spec §4.2). Only the registry creates descriptors, so
/// every instance has passed its rules.
/// </summary>
public sealed class ShardDescriptor
{
    internal ShardDescriptor(
        ShardId id,
        ShardAlias alias,
        string canonicalBaseUrl,
        IReadOnlyList<string> canonicalLinkBaseAliases,
        BackendProfile profile,
        ShardAuth auth,
        int ordinal)
    {
        Id = id;
        Alias = alias;
        CanonicalBaseUrl = canonicalBaseUrl;
        CanonicalLinkBaseAliases = canonicalLinkBaseAliases;
        BaseUrl = new Uri(canonicalBaseUrl);
        LinkBaseAliases = [.. canonicalLinkBaseAliases.Select(a => new Uri(a))];
        Profile = profile;
        Auth = auth;
        Ordinal = ordinal;
    }

    /// <summary>Gets the shard identifier.</summary>
    public ShardId Id { get; }

    /// <summary>Gets the shard alias.</summary>
    public ShardAlias Alias { get; }

    /// <summary>
    /// Gets the canonical base URL as a <see cref="Uri"/>: https, lowercase host, no default port, no terminal slash,
    /// path case preserved (design spec §4.2).
    /// </summary>
    /// <remarks>
    /// Do not build request or full URLs from this <see cref="Uri"/>. Relative resolution such as
    /// <c>new Uri(BaseUrl, "Patient")</c> drops the last path segment (<c>/fhir</c>); the text of a root base ends in
    /// <c>/</c>, so appending <c>/Patient</c> doubles the slash; and <see cref="Uri.ToString"/> un-escapes the path.
    /// Concatenate onto <see cref="CanonicalBaseUrl"/> instead.
    /// </remarks>
    public Uri BaseUrl { get; }

    /// <summary>
    /// Gets the exact canonical text of <see cref="BaseUrl"/>, with no terminal slash, so a path is appended as
    /// <c>{CanonicalBaseUrl}/{type}</c>. Percent-escapes keep their encoding, with uppercase hex digits. Full URLs and the
    /// registry version are built from this text.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1056:URI-like properties should not be strings",
        Justification = "The exact canonical text; a Uri un-escapes the path in ToString and re-resolves relative paths.")]
    public string CanonicalBaseUrl { get; }

    /// <summary>
    /// Gets the other canonical base forms the shard is known by (custom domain, private-link host), in configured
    /// order (design spec §4.2). The same <see cref="BaseUrl"/> caution applies; use <see cref="CanonicalLinkBaseAliases"/>
    /// for text.
    /// </summary>
    public IReadOnlyList<Uri> LinkBaseAliases { get; }

    /// <summary>Gets the exact canonical text of <see cref="LinkBaseAliases"/>, in configured order.</summary>
    public IReadOnlyList<string> CanonicalLinkBaseAliases { get; }

    /// <summary>Gets the certified facts of the shard's backend.</summary>
    public BackendProfile Profile { get; }

    /// <summary>Gets the federation's read-only credential for the shard (design spec §10.2).</summary>
    public ShardAuth Auth { get; }

    /// <summary>
    /// Gets the zero-based position of the shard in configuration: the unsorted emission order and the sort
    /// tie-break (design spec §4.2).
    /// </summary>
    public int Ordinal { get; }
}

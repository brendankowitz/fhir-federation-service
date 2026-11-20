using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// Classifies <c>Reference.reference</c> literals for chains, includes and revincludes (design spec §7.7). It reads text
/// only: it fetches nothing and evaluates no FHIRPath.
/// </summary>
/// <remarks>
/// <para>
/// A reference is routed only when it is plainly a resource URL: a relative <c>Type/id</c> (optionally
/// <c>/_history/version</c>) belongs to the containing shard, and an absolute URL under a registered base URL or link base
/// alias belongs to the shard registered for that base. The authority uses the registry's normalisation; the path
/// is matched as text so URI resolution cannot turn a malformed path into a local reference.
/// </para>
/// <para>
/// Type, id and version are case-sensitive R4 values (<see cref="ResourceType"/>, <see cref="ResourceId"/>). A persisted
/// <c>(type, id)</c> is only meaningful with its shard, so a result names a <see cref="ResourceKey"/>. Instances are
/// immutable and safe to share across threads.
/// </para>
/// </remarks>
public sealed class ReferenceClassifier
{
    private const string HttpsPrefix = "https://";
    private const string HistorySegment = "_history";

    private readonly ShardRegistry _registry;

    // Longest first, so the most specific base wins when a shard registers a base nested under another of its own.
    private readonly BaseForm[] _bases;
    private readonly HashSet<string> _authorities;

    /// <summary>Creates a classifier over the registered bases of <paramref name="registry"/>.</summary>
    /// <param name="registry">The shard registry; its base URLs and link base aliases decide which absolute references are known.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    public ReferenceClassifier(ShardRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _bases =
        [
            .. registry.Shards
                .SelectMany(s => s.CanonicalLinkBaseAliases.Prepend(s.CanonicalBaseUrl).Select(b => new BaseForm(b, s.Id)))
                .OrderByDescending(b => b.Canonical.Length),
        ];
        _authorities = [.. _bases.Select(b => b.Authority)];
    }

    /// <summary>Classifies the <c>reference</c> of one <c>Reference</c> found in a resource held by a shard.</summary>
    /// <param name="containingShard">The shard that holds the resource containing the reference; relative references belong to it.</param>
    /// <param name="reference">
    /// The <c>Reference.reference</c> text, or <see langword="null"/> when the reference has none (identifier-only or
    /// display-only). Not trimmed or otherwise changed.
    /// </param>
    /// <returns>The classification; it keeps <paramref name="reference"/> verbatim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="containingShard"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="containingShard"/> is not in the registry.</exception>
    public ReferenceClassification Classify(ShardId containingShard, string? reference)
    {
        ArgumentNullException.ThrowIfNull(containingShard);
        if (!_registry.TryGetById(containingShard, out _))
        {
            throw new ArgumentException("The containing shard must be in the registry.", nameof(containingShard));
        }

        if (reference is null)
        {
            return ReferenceClassification.Unrouted(ReferenceKind.IdentifierOnly, null);
        }

        if (reference.Length == 0 || HasUnsafeCharacter(reference))
        {
            return ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
        }

        if (reference[0] == '#')
        {
            return ReferenceClassification.Unrouted(ReferenceKind.Contained, reference);
        }

        // A query or fragment is a search or an anchor, not the identity of one resource.
        if (reference.AsSpan().IndexOfAny('?', '#') >= 0)
        {
            return ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
        }

        int schemeLength = SchemeLength(reference);
        if (schemeLength > 0)
        {
            return ClassifyAbsolute(reference, schemeLength);
        }

        return TryParseResource(reference, containingShard, out ResourceKey? target, out string? version)
            ? ReferenceClassification.Resolved(ReferenceKind.Relative, reference, target, version)
            : ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
    }

    private ReferenceClassification ClassifyAbsolute(string reference, int schemeLength)
    {
        // Registered bases are https only, so any other scheme (http, urn:uuid, ...) is never one of ours.
        if (!reference.AsSpan(0, schemeLength).Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return ReferenceClassification.Unrouted(ReferenceKind.ForeignAbsolute, reference);
        }

        string afterScheme = reference[(schemeLength + 1)..];
        if (!afterScheme.StartsWith("//", StringComparison.Ordinal))
        {
            return ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
        }

        int pathStart = afterScheme.IndexOf('/', 2, StringComparison.Ordinal);
        string authority = pathStart < 0 ? afterScheme[2..] : afterScheme[2..pathStart];
        string path = pathStart < 0 ? string.Empty : afterScheme[pathStart..];
        if (!TryNormalizeAuthority(authority, out string normalizedAuthority))
        {
            return ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
        }

        string candidate = HttpsPrefix + normalizedAuthority + UppercaseEscapes(path);
        foreach (BaseForm form in _bases)
        {
            if (!IsUnderBase(candidate, form.Canonical))
            {
                continue;
            }

            string remainder = candidate.Length > form.Canonical.Length ? candidate[(form.Canonical.Length + 1)..] : string.Empty;
            return TryParseResource(remainder, form.Shard, out ResourceKey? target, out string? version)
                ? ReferenceClassification.Resolved(ReferenceKind.KnownAbsolute, reference, target, version)
                : ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference);
        }

        // On a registered host but under no registered base: a plain path is another server's URL, but a path that URI
        // normalisation could fold into a registered base (dot or empty segments, escapes) is not trusted either way.
        return _authorities.Contains(normalizedAuthority) && !HasPlainPath(path)
            ? ReferenceClassification.Unrouted(ReferenceKind.Invalid, reference)
            : ReferenceClassification.Unrouted(ReferenceKind.ForeignAbsolute, reference);
    }

    /// <summary>Whether <paramref name="candidate"/> is the base or a path below it, on a whole-segment boundary.</summary>
    private static bool IsUnderBase(string candidate, string baseUrl) =>
        candidate.StartsWith(baseUrl, StringComparison.Ordinal)
        && (candidate.Length == baseUrl.Length || candidate[baseUrl.Length] == '/');

    /// <summary>Parses <c>Type/id</c> or <c>Type/id/_history/version</c> exactly; anything else is not a resource URL.</summary>
    private static bool TryParseResource(string text, ShardId shard, [NotNullWhen(true)] out ResourceKey? key, out string? version)
    {
        key = null;
        version = null;
        string[] segments = text.Split('/');
        if (segments.Length is not (2 or 4)
            || !ResourceType.TryParse(segments[0], out ResourceType? type)
            || !ResourceId.TryParse(segments[1], out ResourceId? id))
        {
            return false;
        }

        if (segments.Length == 4)
        {
            if (segments[2] != HistorySegment || !ResourceId.TryParse(segments[3], out ResourceId? versionId))
            {
                return false;
            }

            version = versionId.Value;
        }

        key = new ResourceKey(shard, type, id);
        return true;
    }

    /// <summary>
    /// Spells <paramref name="authority"/> (<c>host[:port]</c>) exactly as the registry spells a base's authority, by
    /// canonicalising it with <see cref="ShardBaseUrl"/> (lowercase host, compressed bracketed IPv6, default port dropped).
    /// Only the authority goes through the URI canonicaliser; the path stays text. User info, an escape, an empty host, an
    /// unbracketed ':' in the host or a port that is not a number from 1 to 65535 is rejected before it gets there, since
    /// <see cref="Uri"/> would accept or reinterpret each of them.
    /// </summary>
    private static bool TryNormalizeAuthority(string authority, out string normalized)
    {
        normalized = string.Empty;
        if (authority.AsSpan().IndexOfAny('@', '%') >= 0)
        {
            return false;
        }

        // A port follows the last ':' that is outside an IPv6 literal's brackets.
        int colon = authority.LastIndexOf(':', StringComparison.Ordinal);
        bool hasPort = colon > authority.LastIndexOf(']', StringComparison.Ordinal);
        string host = hasPort ? authority[..colon] : authority;
        bool bracketed = host.StartsWith('[', StringComparison.Ordinal) && host.EndsWith(']', StringComparison.Ordinal);
        if (host.Length == 0 || (!bracketed && host.AsSpan().IndexOfAny('[', ']', ':') >= 0))
        {
            return false;
        }

        if (hasPort)
        {
            string digits = authority[(colon + 1)..];
            if (digits.Length is 0 or > 5
                || !digits.All(char.IsAsciiDigit)
                || int.Parse(digits, CultureInfo.InvariantCulture) is 0 or > 65535)
            {
                return false;
            }
        }

        if (ShardBaseUrl.Canonicalize(HttpsPrefix + authority, out string canonical) is not null)
        {
            return false;
        }

        normalized = canonical[HttpsPrefix.Length..];
        return true;
    }

    /// <summary>The length of an RFC 3986 scheme that starts <paramref name="reference"/> (before its ':'), or 0 when there is none.</summary>
    private static int SchemeLength(string reference)
    {
        if (!char.IsAsciiLetter(reference[0]))
        {
            return 0;
        }

        for (int i = 1; i < reference.Length; i++)
        {
            char c = reference[i];
            if (c == ':')
            {
                return i;
            }

            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>Whether the text holds a space or control character, a backslash (which Uri reads as '/') or a non-ASCII character.</summary>
    private static bool HasUnsafeCharacter(string text)
    {
        foreach (char c in text)
        {
            if (c <= ' ' || c >= 0x7F || c == '\\')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A path with no escape, no empty segment (except a final one) and no dot segment, which URI resolution leaves alone.</summary>
    private static bool HasPlainPath(string path)
    {
        if (path.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = path.Split('/');
        for (int i = 1; i < segments.Length; i++)
        {
            if ((segments[i].Length == 0 && i < segments.Length - 1) || segments[i] is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Uppercases the hex digits of each complete percent-escape, as the registry spells its bases.</summary>
    private static string UppercaseEscapes(string path)
    {
        char[] chars = path.ToCharArray();
        for (int i = 0; i < chars.Length - 2; i++)
        {
            if (chars[i] == '%' && char.IsAsciiHexDigit(chars[i + 1]) && char.IsAsciiHexDigit(chars[i + 2]))
            {
                chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
                chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
            }
        }

        return new string(chars);
    }

    private readonly record struct BaseForm(string Canonical, ShardId Shard)
    {
        /// <summary>Gets the <c>host[:port]</c> of the base.</summary>
        public string Authority
        {
            get
            {
                string afterScheme = Canonical[HttpsPrefix.Length..];
                int slash = afterScheme.IndexOf('/', StringComparison.Ordinal);
                return slash < 0 ? afterScheme : afterScheme[..slash];
            }
        }
    }
}

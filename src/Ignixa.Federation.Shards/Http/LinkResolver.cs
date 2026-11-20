using System.Globalization;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Decides whether backend <c>next</c> link text continues this shard's own typed search, and if so extracts the query to
/// redeem at the configured base (design spec §7.4). Pure text rules, failing closed: the link is never fetched as given,
/// so it cannot move the credential to another origin, path or shard.
/// </summary>
/// <remarks>
/// <para>Absolute links must name the configured base or a configured link base alias (https only; host compared without
/// case; port 443 when absent) followed by <c>/{type}</c> exactly. Path-absolute links must be the base path followed by
/// <c>/{type}</c>. A relative <c>{type}?…</c> or <c>?…</c> link is read against the service base, the FHIR convention for
/// relative references, rather than against the request URL, whose path differs for <c>_search</c>.</para>
/// <para>Anything else — another scheme, credentials or escapes in the authority, a non-canonical port, dot segments,
/// backslashes, a fragment, a missing or empty query, or text that is not visible ASCII — is not a continuation.</para>
/// </remarks>
internal sealed class LinkResolver
{
    private const int HttpsPort = 443;

    private readonly string _basePath;
    private readonly (string Host, int Port, string Path)[] _origins;

    public LinkResolver(ShardDescriptor shard)
    {
        var origins = new List<(string Host, int Port, string Path)>();
        foreach (string canonical in shard.CanonicalLinkBaseAliases.Prepend(shard.CanonicalBaseUrl))
        {
            if (canonical.StartsWith("https://", StringComparison.Ordinal) && TryParseHierarchy(canonical.AsSpan(8), out var origin))
            {
                origins.Add(origin);
            }
        }

        // The registry guarantees an https base, so the first entry is always the base.
        _basePath = origins[0].Path;
        _origins = [.. origins];
    }

    /// <summary>Returns the raw query of <paramref name="link"/> when it continues the <paramref name="type"/> search; otherwise <see langword="null"/>.</summary>
    public string? QueryOf(ResourceType type, string link)
    {
        if (!IsLinkText(link))
        {
            return null;
        }

        int questionMark = link.IndexOf('?', StringComparison.Ordinal);
        if (questionMark < 0)
        {
            return null;
        }

        string query = link[(questionMark + 1)..];
        if (query.Length == 0 || !QueryText.IsWireSafe(query))
        {
            return null;
        }

        string typedSuffix = "/" + type.Value;
        ReadOnlySpan<char> head = link.AsSpan(0, questionMark);
        bool continues = head switch
        {
            [] => true,
            ['/', '/', ..] => IsOwnHierarchy(head[2..], typedSuffix),
            ['/', ..] => head.SequenceEqual(_basePath + typedSuffix),
            _ when SchemeLength(head) is var scheme and > 0 =>
                head[..scheme].Equals("https", StringComparison.OrdinalIgnoreCase)
                && head[(scheme + 1)..].StartsWith("//", StringComparison.Ordinal)
                && IsOwnHierarchy(head[(scheme + 3)..], typedSuffix),
            _ => head.SequenceEqual(type.Value),
        };

        return continues ? query : null;
    }

    // Visible ASCII only; no fragment, and no backslash, which some parsers read as a path separator.
    private static bool IsLinkText(string link)
    {
        foreach (char c in link)
        {
            if (c is < '!' or > '~' or '#' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    // RFC 3986: a scheme is the text before the first ':' when no '/' comes before it.
    private static int SchemeLength(ReadOnlySpan<char> head)
    {
        int colon = head.IndexOf(':');
        return colon > 0 && head[..colon].IndexOf('/') < 0 ? colon : -1;
    }

    // Parses "authority/path" with no user info and no escapes in the authority.
    private static bool TryParseHierarchy(ReadOnlySpan<char> text, out (string Host, int Port, string Path) origin)
    {
        origin = default;
        int slash = text.IndexOf('/');
        ReadOnlySpan<char> authority = slash < 0 ? text : text[..slash];
        string path = slash < 0 ? string.Empty : text[slash..].ToString();
        if (authority.IsEmpty || authority.IndexOfAny('@', '%') >= 0)
        {
            return false;
        }

        ReadOnlySpan<char> host;
        ReadOnlySpan<char> portText;
        if (authority[0] == '[')
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = authority[..(close + 1)];
            ReadOnlySpan<char> rest = authority[(close + 1)..];
            if (!rest.IsEmpty && rest[0] != ':')
            {
                return false;
            }

            portText = rest.IsEmpty ? [] : rest[1..];
            if (rest.Length == 1)
            {
                return false;
            }
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? [] : authority[(colon + 1)..];
            if (colon >= 0 && portText.IsEmpty)
            {
                return false;
            }
        }

        if (host.IsEmpty || !TryParsePort(portText, out int port))
        {
            return false;
        }

        origin = (host.ToString(), port, path);
        return true;
    }

    // Absent means 443; present must be canonical digits (no sign, no leading zero) in range.
    private static bool TryParsePort(ReadOnlySpan<char> text, out int port)
    {
        port = HttpsPort;
        if (text.IsEmpty)
        {
            return true;
        }

        return text.Length <= 5
            && text[0] != '0'
            && !text.ContainsAnyExceptInRange('0', '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port <= ushort.MaxValue;
    }

    private bool IsOwnHierarchy(ReadOnlySpan<char> hierarchy, string typedSuffix)
    {
        if (!TryParseHierarchy(hierarchy, out var link))
        {
            return false;
        }

        foreach (var origin in _origins)
        {
            if (link.Port == origin.Port
                && link.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase)
                && link.Path.Equals(origin.Path + typedSuffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

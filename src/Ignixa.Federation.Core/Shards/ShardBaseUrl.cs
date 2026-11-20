namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// The one canonical spelling of a shard base URL or link alias (design spec §4.2), so that equal bases compare equal
/// as text and the registry version does not change with spelling.
/// </summary>
internal static class ShardBaseUrl
{
    /// <summary>
    /// Canonicalises <paramref name="value"/>: https only; no userinfo, query, fragment, dot segments, empty inner
    /// segments or trailing-dot host; lowercase scheme and host; no default port; path case kept with uppercase
    /// percent-escape digits; no terminal slash.
    /// </summary>
    /// <returns>Why <paramref name="value"/> is rejected, or <see langword="null"/> when <paramref name="canonical"/> is set.</returns>
    internal static string? Canonicalize(string? value, out string canonical)
    {
        canonical = string.Empty;

        // Uri turns '\' into '/' and resolves dot segments, so both are checked on the raw text.
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || value.Contains('\\', StringComparison.Ordinal)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "must be an absolute https URL.";
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return "must not contain user info, a query or a fragment.";
        }

        // 'host.' and 'host' name the same server; one spelling is accepted so they cannot pass as two bases.
        if (uri.Host.EndsWith('.', StringComparison.Ordinal))
        {
            return "must not have a host ending in '.'.";
        }

        if (!HasCleanSegments(value.Trim()))
        {
            return "must not contain '.' or '..' segments or empty segments.";
        }

        // IdnHost gives the punycode form of a name but drops the brackets of an IPv6 literal.
        string host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        string port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        string path = uri.AbsolutePath.EndsWith('/', StringComparison.Ordinal) ? uri.AbsolutePath[..^1] : uri.AbsolutePath;
        canonical = $"https://{host}{port}{UppercaseEscapes(path)}";
        return null;
    }

    /// <summary>
    /// Uppercases the hex digits of every percent-escape (RFC 3986 §6.2.2.1), so <c>%2f</c> and <c>%2F</c> are one base.
    /// Uri has already decoded escaped unreserved characters and escaped any bare <c>%</c>, so every <c>%</c> in
    /// <paramref name="path"/> starts a two-digit escape.
    /// </summary>
    private static string UppercaseEscapes(string path)
    {
        char[] chars = path.ToCharArray();
        for (int i = 0; i < chars.Length - 2; i++)
        {
            if (chars[i] == '%')
            {
                chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
                chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Checks the path as written, before Uri resolves it: no dot segment, escaped or not, and no empty segment except
    /// the one a terminal slash leaves.
    /// </summary>
    private static bool HasCleanSegments(string value)
    {
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        int pathStart = value.IndexOf('/', authorityStart, StringComparison.Ordinal);
        if (pathStart < 0)
        {
            return true;
        }

        string[] segments = value[(pathStart + 1)..].Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = Uri.UnescapeDataString(segments[i]);
            if ((segment.Length == 0 && i < segments.Length - 1) || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}

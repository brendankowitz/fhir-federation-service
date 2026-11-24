namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// A <c>next</c> link the backend issued, accepted only when it points at the exact local fixture origin and
/// the <c>Patient</c> path. It keeps the encoded query suffix verbatim; the harness replays that text and
/// never splits, decodes, reorders or merges it. <see cref="Create"/> is the only way to obtain one, so no
/// request is built from a link that could reach another host, port, scheme or path.
/// </summary>
internal sealed class NativeContinuation
{
    private NativeContinuation(string query)
    {
        Query = query;
    }

    /// <summary>The encoded query suffix exactly as issued, without the leading <c>?</c>.</summary>
    public string Query { get; }

    /// <exception cref="BackendEvidenceException">The link is not on the exact local origin and Patient path, or its query is ambiguous.</exception>
    public static NativeContinuation Create(string url, Uri baseAddress)
    {
        string prefix = $"{baseAddress.GetLeftPart(UriPartial.Authority)}/Patient?";
        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw SearchsetJson.Incomplete("a next link is not on the fixture origin and Patient path");
        }

        string query = url[prefix.Length..];
        if (query.Length == 0 || query.Any(c => c is < '\u0021' or > '\u007E' or '#' or '\\') || HasMalformedEscape(query))
        {
            throw SearchsetJson.Incomplete("a next link has no query or one that is not plain encoded text");
        }

        return new NativeContinuation(query);
    }

    /// <summary>Whether a <c>%</c> is not followed by two hex digits. Escapes are never decoded; this only keeps broken ones off the wire.</summary>
    private static bool HasMalformedEscape(string query)
    {
        for (int i = query.IndexOf('%', StringComparison.Ordinal); i >= 0; i = query.IndexOf('%', i + 1, StringComparison.Ordinal))
        {
            if (i + 2 >= query.Length || !char.IsAsciiHexDigit(query[i + 1]) || !char.IsAsciiHexDigit(query[i + 2]))
            {
                return true;
            }
        }

        return false;
    }
}

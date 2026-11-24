namespace Ignixa.Federation.Shards.Http;

/// <summary>
/// Rules for search query text the client sends verbatim and reads back from <c>self</c> and <c>next</c> links.
/// </summary>
/// <remarks>
/// Pairs are compared after form decoding (<c>+</c> is a space, <c>%HH</c> is a byte), each decoded byte held as one
/// <see langword="char"/>, so two spellings of the same bytes compare equal and nothing depends on a text encoding.
/// </remarks>
internal static class QueryText
{
    /// <summary>
    /// Gets whether <paramref name="text"/> can go on the wire unchanged: visible ASCII other than <c>#</c>, with every
    /// <c>%</c> starting a two-hex-digit escape. Anything else would be re-encoded or misread on the way.
    /// </summary>
    public static bool IsWireSafe(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is < '!' or > '~' or '#')
            {
                return false;
            }

            if (c == '%' && (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether <paramref name="query"/> has a named parameter: some <c>&amp;</c>-separated segment that is neither empty
    /// nor starts at its <c>=</c>. A query of only separators, or whose segments all lack a name, supplies nothing to
    /// continue with. A name with an empty value counts; the text is not decoded, so the server's spelling is untouched.
    /// </summary>
    public static bool HasNamedParameter(ReadOnlySpan<char> query)
    {
        foreach (Range range in query.Split('&'))
        {
            ReadOnlySpan<char> segment = query[range];
            if (!segment.IsEmpty && segment[0] != '=')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Form-decodes a query into its <c>key=value</c> pairs. Empty segments are skipped; a segment without <c>=</c> has an
    /// empty value.
    /// </summary>
    /// <returns><see langword="false"/> when the query is not wire-safe text.</returns>
    public static bool TryDecodePairs(ReadOnlySpan<char> query, out List<(string Key, string Value)> pairs)
    {
        pairs = [];
        if (!IsWireSafe(query))
        {
            return false;
        }

        foreach (Range range in query.Split('&'))
        {
            ReadOnlySpan<char> segment = query[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            int equals = segment.IndexOf('=');
            pairs.Add(equals < 0
                ? (Decode(segment), string.Empty)
                : (Decode(segment[..equals]), Decode(segment[(equals + 1)..])));
        }

        return true;
    }

    /// <summary>Gets whether two wire-safe queries decode to the same pairs, counting duplicates, in any order.</summary>
    public static bool SamePairs(ReadOnlySpan<char> left, ReadOnlySpan<char> right) =>
        TryDecodePairs(left, out List<(string Key, string Value)> a)
        && TryDecodePairs(right, out List<(string Key, string Value)> b)
        && SameMultiset(a, b);

    /// <summary>Gets whether two pair lists hold the same pairs with the same counts, ignoring order.</summary>
    public static bool SameMultiset(List<(string Key, string Value)> left, List<(string Key, string Value)> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        Comparison<(string Key, string Value)> ordinal = static (x, y) =>
            string.CompareOrdinal(x.Key, y.Key) is var byKey and not 0 ? byKey : string.CompareOrdinal(x.Value, y.Value);
        List<(string Key, string Value)> a = [.. left];
        List<(string Key, string Value)> b = [.. right];
        a.Sort(ordinal);
        b.Sort(ordinal);
        return a.SequenceEqual(b);
    }

    // Callers have checked IsWireSafe, so every '%' starts a valid escape.
    private static string Decode(ReadOnlySpan<char> encoded)
    {
        return string.Create(DecodedLength(encoded), encoded.ToString(), static (destination, source) =>
        {
            int written = 0;
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '%')
                {
                    destination[written++] = (char)((HexValue(source[i + 1]) << 4) | HexValue(source[i + 2]));
                    i += 2;
                }
                else
                {
                    destination[written++] = c == '+' ? ' ' : c;
                }
            }
        });
    }

    private static int DecodedLength(ReadOnlySpan<char> encoded) => encoded.Length - (2 * encoded.Count('%'));

    private static int HexValue(char digit) => digit switch
    {
        <= '9' => digit - '0',
        <= 'F' => digit - 'A' + 10,
        _ => digit - 'a' + 10,
    };
}

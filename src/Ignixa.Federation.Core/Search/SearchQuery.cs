using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Unicode;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// A search query as it arrived on the wire: an ordered list of <see cref="QueryOccurrence"/> pairs, each with its raw text
/// and its once-decoded key and value (design spec §6.1). Nothing is grouped, deduplicated, trimmed or reordered, so the
/// planner sees every repeated key and a rewrite can forward the original text.
/// </summary>
/// <remarks>
/// Two entry points share one parser and differ only in the leading <c>?</c>: <see cref="Parse"/> is for a <c>GET</c> query
/// string, where <c>?</c> is the URL delimiter and is dropped; <see cref="ParseForm"/> is for an
/// <c>application/x-www-form-urlencoded</c> body, where every character is data. The host validates the content type and
/// picks the entry point. Only <c>&amp;</c> separates pairs (a <c>;</c> is ordinary text). Any wire form that would need a
/// guess is a <see cref="SearchFailure"/>, not a skipped pair.
/// </remarks>
public sealed class SearchQuery
{
    private static readonly SearchQuery Empty = new([]);

    private SearchQuery(IReadOnlyList<QueryOccurrence> occurrences) => Occurrences = occurrences;

    /// <summary>Gets every occurrence in wire order. Empty for an empty query; empty values are kept.</summary>
    public IReadOnlyList<QueryOccurrence> Occurrences { get; }

    /// <summary>Parses the query string of a <c>GET</c> request.</summary>
    /// <param name="encoded">
    /// The wire text, such as <c>?name=a%2Cb&amp;_count=5</c>. One leading <c>?</c> is dropped. Every
    /// <c>&amp;</c>-separated segment must be <c>key=value</c> with a non-empty key, an empty value allowed, and only
    /// printable ASCII; <c>+</c> decodes to a space and <c>%XX</c> to its UTF-8 byte, once.
    /// </param>
    /// <returns>The query, or the first failure with its position; the failure never repeats the text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> is <see langword="null"/>.</exception>
    public static SearchQueryParseResult Parse(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return ParsePairs(encoded.StartsWith('?', StringComparison.Ordinal) ? encoded.AsSpan(1) : encoded);
    }

    /// <summary>Parses an <c>application/x-www-form-urlencoded</c> body, such as that of <c>POST [type]/_search</c>.</summary>
    /// <param name="encoded">
    /// The body text. Nothing is stripped: a leading <c>?</c> stays in the first key (<c>?name</c>), for the planner to
    /// reject as an unsupported parameter. Otherwise the rules of <see cref="Parse"/> apply.
    /// </param>
    /// <returns>The query, or the first failure with its position; the failure never repeats the text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> is <see langword="null"/>.</exception>
    public static SearchQueryParseResult ParseForm(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return ParsePairs(encoded);
    }

    private static SearchQueryParseResult ParsePairs(ReadOnlySpan<char> rest)
    {
        if (rest.IsEmpty)
        {
            return SearchQueryParseResult.Success(Empty);
        }

        var occurrences = new List<QueryOccurrence>();
        for (int ordinal = 0; ; ordinal++)
        {
            int end = rest.IndexOf('&');
            ReadOnlySpan<char> pair = end < 0 ? rest : rest[..end];

            if (!TryParsePair(ordinal, pair, out QueryOccurrence? occurrence, out SearchFailureCode code))
            {
                return SearchQueryParseResult.Fail(new SearchFailure(code, ordinal));
            }

            occurrences.Add(occurrence);
            if (end < 0)
            {
                return SearchQueryParseResult.Success(new SearchQuery(occurrences.AsReadOnly()));
            }

            rest = rest[(end + 1)..];
        }
    }

    private static bool TryParsePair(int ordinal, ReadOnlySpan<char> pair, [NotNullWhen(true)] out QueryOccurrence? occurrence, out SearchFailureCode code)
    {
        occurrence = null;
        if (pair.IsEmpty)
        {
            code = SearchFailureCode.EmptyPair;
            return false;
        }

        foreach (char c in pair)
        {
            if (c is < '!' or > '~' or '#')
            {
                code = SearchFailureCode.InvalidCharacter;
                return false;
            }
        }

        int equals = pair.IndexOf('=');
        if (equals < 0)
        {
            code = SearchFailureCode.MissingEquals;
            return false;
        }

        if (equals == 0)
        {
            code = SearchFailureCode.EmptyKey;
            return false;
        }

        ReadOnlySpan<char> rawKey = pair[..equals];
        ReadOnlySpan<char> rawValue = pair[(equals + 1)..];
        if (!TryDecode(rawKey, out string? key, out code) || !TryDecode(rawValue, out string? value, out code))
        {
            return false;
        }

        occurrence = new QueryOccurrence(ordinal, new ParameterOccurrence(rawKey.ToString(), rawValue.ToString()), key, value);
        return true;
    }

    // Input is printable ASCII, so each char is one byte; '+' and %XX produce the bytes, and UTF-8 is checked once at the end.
    private static bool TryDecode(ReadOnlySpan<char> text, [NotNullWhen(true)] out string? decoded, out SearchFailureCode code)
    {
        decoded = null;
        code = default;
        var bytes = new byte[text.Length];
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '%')
            {
                if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2]))
                {
                    code = SearchFailureCode.MalformedPercentEncoding;
                    return false;
                }

                bytes[length++] = (byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2]));
                i += 2;
            }
            else
            {
                bytes[length++] = c == '+' ? (byte)' ' : (byte)c;
            }
        }

        ReadOnlySpan<byte> utf8 = bytes.AsSpan(0, length);
        if (!Utf8.IsValid(utf8))
        {
            code = SearchFailureCode.InvalidUtf8;
            return false;
        }

        decoded = Encoding.UTF8.GetString(utf8);
        return true;
    }

    private static int HexValue(char digit) => char.IsAsciiDigit(digit) ? digit - '0' : (char.ToUpperInvariant(digit) - 'A') + 10;
}

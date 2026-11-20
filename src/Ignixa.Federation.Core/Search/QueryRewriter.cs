using System.Text;

namespace Ignixa.Federation.Core.Search;

/// <summary>
/// Rewrites a <see cref="SearchQuery"/> by keeping, dropping or replacing whole occurrences, chosen by ordinal (design spec
/// §6.1). Untouched occurrences keep their original raw text; no tree is serialised and nothing is decoded again.
/// </summary>
/// <remarks>
/// Edits are by <see cref="QueryOccurrence.Ordinal"/>, never by key: <c>code=a&amp;code=b</c> has two occurrences of one key.
/// A replacement stands in the dropped occurrence's position. Request controls such as the page size are not added here;
/// the executor appends them separately. Not thread-safe.
/// </remarks>
public sealed class QueryRewriter
{
    private readonly SearchQuery _query;
    private readonly Dictionary<int, ParameterOccurrence?> _edits = [];

    /// <summary>Initializes a new instance of the <see cref="QueryRewriter"/> class with every occurrence kept.</summary>
    /// <param name="query">The query to rewrite.</param>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    public QueryRewriter(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
    }

    /// <summary>Encodes one planner-generated pair, percent-encoding the decoded key and value once each.</summary>
    /// <param name="decodedKey">The key as text, such as <c>name:exact</c>. Not empty.</param>
    /// <param name="decodedValue">The value as text, such as <c>a,b</c>. May be empty.</param>
    /// <returns>An occurrence that a backend decodes back to exactly these components.</returns>
    /// <remarks>
    /// Everything but the RFC 3986 unreserved characters (<c>A-Z a-z 0-9 - . _ ~</c>) is written as <c>%XX</c> of its UTF-8
    /// bytes, so a space is <c>%20</c>, a plus is <c>%2B</c>, and the <c>:</c> of a modifier is <c>%3A</c>, which a backend
    /// decodes before it reads the modifier. Pass text that is already decoded; encoded text would be encoded twice.
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="decodedKey"/> is empty, or either text has an unpaired surrogate.</exception>
    public static ParameterOccurrence Encode(string decodedKey, string decodedValue)
    {
        ArgumentNullException.ThrowIfNull(decodedKey);
        ArgumentNullException.ThrowIfNull(decodedValue);
        return new ParameterOccurrence(PercentEncode(decodedKey, nameof(decodedKey)), PercentEncode(decodedValue, nameof(decodedValue)));
    }

    /// <summary>Joins occurrences as <c>Key=Value</c> pairs separated by <c>&amp;</c>, without a leading <c>?</c>.</summary>
    /// <param name="occurrences">The occurrences to render, in order.</param>
    /// <returns>Text for a query string or form body; empty when there are none.</returns>
    public static string Render(IEnumerable<ParameterOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        return string.Join('&', occurrences.Select(o => $"{o.Key}={o.Value}"));
    }

    /// <summary>Removes the occurrence at <paramref name="ordinal"/>.</summary>
    /// <param name="ordinal">The <see cref="QueryOccurrence.Ordinal"/> to drop.</param>
    /// <exception cref="ArgumentOutOfRangeException">No occurrence has that ordinal.</exception>
    /// <exception cref="ArgumentException">That occurrence was already dropped or replaced.</exception>
    public void Drop(int ordinal) => Edit(ordinal, null);

    /// <summary>Replaces the occurrence at <paramref name="ordinal"/> with <paramref name="replacement"/>, in the same position.</summary>
    /// <param name="ordinal">The <see cref="QueryOccurrence.Ordinal"/> to replace.</param>
    /// <param name="replacement">The generated occurrence, usually from <see cref="Encode"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">No occurrence has that ordinal.</exception>
    /// <exception cref="ArgumentException">That occurrence was already dropped or replaced.</exception>
    public void Replace(int ordinal, ParameterOccurrence replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        Edit(ordinal, replacement);
    }

    /// <summary>Applies the edits so far.</summary>
    /// <returns>The kept, replaced and original occurrences in the original order.</returns>
    public IReadOnlyList<ParameterOccurrence> Build()
    {
        var result = new List<ParameterOccurrence>(_query.Occurrences.Count);
        foreach (QueryOccurrence occurrence in _query.Occurrences)
        {
            if (!_edits.TryGetValue(occurrence.Ordinal, out ParameterOccurrence? edited))
            {
                result.Add(occurrence.Raw);
            }
            else if (edited is not null)
            {
                result.Add(edited);
            }
        }

        return result.AsReadOnly();
    }

    private void Edit(int ordinal, ParameterOccurrence? replacement)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _query.Occurrences.Count);
        if (!_edits.TryAdd(ordinal, replacement))
        {
            throw new ArgumentException("That occurrence was already dropped or replaced.", nameof(ordinal));
        }
    }

    private static string PercentEncode(string text, string paramName)
    {
        byte[] bytes;
        try
        {
            bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Text must be valid Unicode, without an unpaired surrogate.", paramName, ex);
        }

        var encoded = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z') or (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
            {
                encoded.Append((char)b);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}

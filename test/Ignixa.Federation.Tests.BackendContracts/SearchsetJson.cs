using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The searchset shape rules shared by every evidence parser. Every failure is an
/// <see cref="BackendFailureKind.IncompleteSearch"/> with fixed text: nothing from the response is echoed.
/// </summary>
internal static class SearchsetJson
{
    /// <summary>
    /// Parses <paramref name="json"/> as a searchset Bundle with well-formed links and no <c>next</c> link.
    /// The caller owns and disposes the returned document.
    /// </summary>
    public static JsonDocument ParseCompleteSearchset(string json, bool countOnly = false)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = !countOnly });
        }
        catch (JsonException)
        {
            throw Incomplete("response body is not valid JSON");
        }

        try
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || StringMember(root, "resourceType") != "Bundle"
                || StringMember(root, "type") != "searchset")
            {
                throw Incomplete("response is not a searchset Bundle");
            }

            foreach (JsonElement link in Array(root, "link"))
            {
                if (StringMember(link, "relation") is not { Length: > 0 } relation)
                {
                    throw Incomplete("a searchset link has no relation");
                }

                if (relation == "next")
                {
                    throw Incomplete("a next link is present, so one page does not hold every match");
                }

                if (countOnly && StringMember(link, "url") is not { Length: > 0 })
                {
                    throw Incomplete("a count searchset link has no URL");
                }
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static BackendEvidenceException Incomplete(string detail) => new(BackendFailureKind.IncompleteSearch, detail);

    /// <summary>
    /// The decoded name and value of every parameter in the single self link, in order and with multiplicity.
    /// The query is split on <c>&amp;</c> and <c>=</c> before decoding, so an encoded delimiter stays data, and
    /// the URL is never requested. The caller passes the root of a document parsed by
    /// <see cref="ParseCompleteSearchset"/> with <c>countOnly</c>, so every link has a URL.
    /// </summary>
    public static List<(string Name, string Value)> SelfQueryPairs(JsonElement root)
    {
        string[] selfUrls = [.. Array(root, "link")
            .Where(link => StringMember(link, "relation") == "self")
            .Select(link => StringMember(link, "url")!)];
        if (selfUrls.Length != 1)
        {
            throw Incomplete($"the searchset holds {selfUrls.Length} self links, not exactly one");
        }

        string url = selfUrls[0];
        if (!Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute))
        {
            throw Incomplete("the self link is not a well-formed URI reference");
        }

        int fragmentStart = url.IndexOf('#', StringComparison.Ordinal);
        int queryStart = url.IndexOf('?', StringComparison.Ordinal);
        int queryEnd = fragmentStart < 0 ? url.Length : fragmentStart;
        string query = queryStart >= 0 && queryStart < queryEnd ? url[(queryStart + 1)..queryEnd] : string.Empty;

        List<(string Name, string Value)> pairs = [];
        foreach (string parameter in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = parameter.IndexOf('=', StringComparison.Ordinal);
            pairs.Add((
                Decode(separator < 0 ? parameter : parameter[..separator]),
                Decode(separator < 0 ? string.Empty : parameter[(separator + 1)..])));
        }

        return pairs;
    }

    private static string Decode(string formComponent) => Uri.UnescapeDataString(formComponent.Replace('+', ' '));

    public static string? StringMember(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The elements of an optional array member. A member of any other type is an error.</summary>
    public static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw Incomplete($"the searchset {name} field is not an array");
    }
}

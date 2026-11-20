using System.Text.Json;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The accounted-for result of one search: every match id, sorted, from a single searchset page
/// that the server says is complete. Construction through <see cref="Parse"/> is the only way to
/// obtain one, so an instance always means "no next link, total equals the match count, and every
/// entry is a match".
/// </summary>
public sealed class SearchsetEvidence
{
    private SearchsetEvidence(IReadOnlyList<string> matchIds)
    {
        MatchIds = matchIds;
    }

    /// <summary>Resource ids of all match entries, in ordinal order. Repeats are kept.</summary>
    public IReadOnlyList<string> MatchIds { get; }

    /// <exception cref="BackendEvidenceException">The response is not a complete searchset.</exception>
    public static SearchsetEvidence Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw Incomplete("response body is not valid JSON");
        }

        using (document)
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
            }

            var ids = new List<string>();
            int nonMatchEntries = 0;
            foreach (JsonElement entry in Array(root, "entry"))
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw Incomplete("a searchset entry is not an object");
                }

                JsonElement resource = entry.TryGetProperty("resource", out JsonElement r) ? r : default;
                string? id = resource.ValueKind == JsonValueKind.Object ? StringMember(resource, "id") : null;
                string? mode = entry.TryGetProperty("search", out JsonElement search) && search.ValueKind == JsonValueKind.Object
                    ? StringMember(search, "mode")
                    : null;

                if (mode == "match" && StringMember(resource, "resourceType") == "Observation" && id is { Length: > 0 })
                {
                    ids.Add(id);
                }
                else
                {
                    nonMatchEntries++;
                }
            }

            if (nonMatchEntries > 0)
            {
                throw Incomplete($"{nonMatchEntries} entries are not Observation matches (include or outcome entries, or entries without an id)");
            }

            if (!root.TryGetProperty("total", out JsonElement total) ||
                total.ValueKind != JsonValueKind.Number || !total.TryGetInt32(out int totalCount))
            {
                throw Incomplete("the searchset has no total, so completeness cannot be checked");
            }

            if (totalCount != ids.Count)
            {
                throw Incomplete($"total is {totalCount} but the page holds {ids.Count} matches");
            }

            ids.Sort(StringComparer.Ordinal);
            return new SearchsetEvidence(ids.AsReadOnly());
        }
    }

    private static BackendEvidenceException Incomplete(string detail) => new(BackendFailureKind.IncompleteSearch, detail);

    private static string? StringMember(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
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

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

    /// <param name="json">The response body.</param>
    /// <param name="resourceType">The type every entry must be a match of.</param>
    /// <exception cref="BackendEvidenceException">The response is not a complete searchset.</exception>
    public static SearchsetEvidence Parse(string json, string resourceType = "Observation")
    {
        using (JsonDocument document = SearchsetJson.ParseCompleteSearchset(json))
        {
            JsonElement root = document.RootElement;
            var ids = new List<string>();
            int nonMatchEntries = 0;
            foreach (JsonElement entry in SearchsetJson.Array(root, "entry"))
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw Incomplete("a searchset entry is not an object");
                }

                JsonElement resource = entry.TryGetProperty("resource", out JsonElement r) ? r : default;
                string? id = resource.ValueKind == JsonValueKind.Object ? SearchsetJson.StringMember(resource, "id") : null;
                string? mode = entry.TryGetProperty("search", out JsonElement search) && search.ValueKind == JsonValueKind.Object
                    ? SearchsetJson.StringMember(search, "mode")
                    : null;

                if (mode == "match" && SearchsetJson.StringMember(resource, "resourceType") == resourceType && id is { Length: > 0 })
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
                throw Incomplete($"{nonMatchEntries} entries are not {resourceType} matches (include or outcome entries, or entries without an id)");
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

    private static BackendEvidenceException Incomplete(string detail) => SearchsetJson.Incomplete(detail);
}

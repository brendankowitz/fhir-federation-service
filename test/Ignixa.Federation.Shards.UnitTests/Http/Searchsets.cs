using System.Text;
using System.Text.Json;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>Writes synthetic searchset JSON as text, so tests control the exact bytes the client receives.</summary>
internal static class Searchsets
{
    public static string Page(string? self, string? next = null, long? total = null, params string[] entries)
    {
        var links = new List<string>();
        if (self is not null)
        {
            links.Add(Link("self", self));
        }

        if (next is not null)
        {
            links.Add(Link("next", next));
        }

        var json = new StringBuilder("{\"resourceType\":\"Bundle\",\"type\":\"searchset\"");
        if (total is { } count)
        {
            json.Append(",\"total\":").Append(count);
        }

        json.Append(",\"link\":[").AppendJoin(',', links).Append(']');
        if (entries.Length > 0)
        {
            json.Append(",\"entry\":[").AppendJoin(',', entries).Append(']');
        }

        return json.Append('}').ToString();
    }

    public static string Entry(string resource, string mode = "match", string? fullUrl = null)
    {
        string full = fullUrl is null ? string.Empty : $"\"fullUrl\":\"{fullUrl}\",";
        return $"{{{full}\"resource\":{resource},\"search\":{{\"mode\":\"{mode}\"}}}}";
    }

    public static string Patient(string id, string versionId = "1", string lastUpdated = "2026-01-02T03:04:05.123Z") =>
        $"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"meta\":{{\"versionId\":\"{versionId}\",\"lastUpdated\":\"{lastUpdated}\"}}}}";

    // The url is JSON-escaped, so a test can put any text (backslash, quote, non-ASCII) in a link.
    private static string Link(string relation, string url) => $"{{\"relation\":\"{relation}\",\"url\":{JsonSerializer.Serialize(url)}}}";
}

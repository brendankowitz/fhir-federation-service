using System.Text.Json.Nodes;

namespace Ignixa.Federation.SampleClient.UnitTests;

/// <summary>Builders for synthetic federation responses (no real data).</summary>
internal static class TestBundles
{
    public const string SourceShardUrl =
        "https://github.com/brendankowitz/fhir-federation-service/StructureDefinition/source-shard";

    public static string Page(string? next = null, params JsonNode[] entries)
    {
        var links = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = "https://fed.example/fhir/Patient" });
        if (next is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = next });
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["link"] = links,
            ["entry"] = new JsonArray(entries),
        }.ToJsonString();
    }

    public static JsonNode Entry(
        string mode, string fullUrl, string type, string id, string? shard = null, string? alternate = null)
    {
        var entry = new JsonObject
        {
            ["fullUrl"] = fullUrl,
            ["resource"] = new JsonObject { ["resourceType"] = type, ["id"] = id },
            ["search"] = new JsonObject { ["mode"] = mode },
        };

        if (shard is not null)
        {
            entry["extension"] = new JsonArray(new JsonObject { ["url"] = SourceShardUrl, ["valueString"] = shard });
        }

        if (alternate is not null)
        {
            entry["link"] = new JsonArray(new JsonObject { ["relation"] = "alternate", ["url"] = alternate });
        }

        return entry;
    }

    public static JsonNode Match(string fullUrl, string type, string id, string? shard = null, string? alternate = null) =>
        Entry("match", fullUrl, type, id, shard, alternate);

    public static JsonNode Include(string fullUrl, string type, string id, string? shard = null) =>
        Entry("include", fullUrl, type, id, shard);

    public static JsonNode OutcomeEntry(string severity, string code, string diagnostics) => new JsonObject
    {
        ["resource"] = JsonNode.Parse(OperationOutcome(severity, code, diagnostics)),
        ["search"] = new JsonObject { ["mode"] = "outcome" },
    };

    public static string OperationOutcome(string severity, string code, string diagnostics) => new JsonObject
    {
        ["resourceType"] = "OperationOutcome",
        ["issue"] = new JsonArray(new JsonObject
        {
            ["severity"] = severity,
            ["code"] = code,
            ["diagnostics"] = diagnostics,
        }),
    }.ToJsonString();
}

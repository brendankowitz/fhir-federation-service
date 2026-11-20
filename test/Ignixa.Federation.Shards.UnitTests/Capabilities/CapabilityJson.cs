using System.Text;
using Ignixa.Federation.Shards.Capabilities;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities;

/// <summary>Builds CapabilityStatement bodies as raw JSON text, so a test can state malformed or ambiguous shapes exactly.</summary>
internal static class CapabilityJson
{
    /// <summary>A valid R4 instance statement; <paramref name="rest"/> is the JSON array text and <paramref name="extraRoot"/> extra root members, each starting with a comma.</summary>
    public static string Statement(string rest = "[]", string extraRoot = "", string fhirVersion = "4.0.1", string kind = "instance") =>
        $$"""{"resourceType":"CapabilityStatement","status":"active","date":"2026-01-01","kind":"{{kind}}","fhirVersion":"{{fhirVersion}}"{{extraRoot}},"rest":{{rest}}}""";

    /// <summary>A statement with one server block holding <paramref name="resources"/> (a JSON array) and common <paramref name="searchParam"/>.</summary>
    public static string ServerStatement(string resources = "[]", string searchParam = "[]", string extraRoot = "") =>
        Statement($$"""[{"mode":"server","resource":{{resources}},"searchParam":{{searchParam}}}]""", extraRoot);

    /// <summary>A statement whose single server resource <paramref name="type"/> advertises search-type and the given members.</summary>
    public static string SearchableResource(string type, string members = "") =>
        $$"""{"type":"{{type}}","interaction":[{"code":"search-type"}]{{members}}}""";

    public static string Param(string name, string type, string? definition = null) =>
        definition is null
            ? $$"""{"name":"{{name}}","type":"{{type}}"}"""
            : $$"""{"name":"{{name}}","type":"{{type}}","definition":"{{definition}}"}""";

    public static string Arr(params string[] elements) => "[" + string.Join(',', elements) + "]";

    public static ShardCapabilityEvidence Read(string json) => CapabilityStatementReader.Read(Encoding.UTF8.GetBytes(json));

    /// <summary>Reads a statement that has exactly one searchable resource, <c>Patient</c>, with <paramref name="members"/>.</summary>
    public static ResourceSearchEvidence ReadPatient(string members) =>
        Read(ServerStatement(Arr(SearchableResource("Patient", members)))).SearchableResources.Values.Single();
}

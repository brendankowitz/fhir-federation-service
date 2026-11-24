using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The seed of the Patient.given backend: six synthetic Patients and nothing else. Each Patient holds one
/// <c>name.given</c> list chosen so that every case in <see cref="GivenCases"/> separates a correct answer from
/// a plausible wrong one: a Patient with both values, one exclusive Patient for each value, a longer value that
/// shares the prefix, a wrong-case value, and a Patient with neither. The expected matches live in
/// <see cref="GivenCases"/> as literals, not in this seed.
/// </summary>
internal static class GivenSeedData
{
    private static readonly string[] PatientIds =
    [
        "given-both",
        "given-case",
        "given-left",
        "given-long",
        "given-neither",
        "given-right",
    ];

    public static readonly BackendSeed Seed = new(
        "Patient",
        PatientIds,
        _ =>
        [
            Patient("given-both", "Quartz", "Mica"),
            Patient("given-left", "Quartz"),
            Patient("given-right", "Mica"),
            Patient("given-long", "Quartzite"),
            Patient("given-case", "QUARTZ"),
            Patient("given-neither", "Topaz"),
        ]);

    public static bool IsReservedId(string id) => Array.IndexOf(PatientIds, id) >= 0;

    private static SeedResource Patient(string id, params string[] given) =>
        new(
            "Patient",
            id,
            new JsonObject
            {
                ["resourceType"] = "Patient",
                ["id"] = id,
                ["name"] = new JsonArray(new JsonObject { ["given"] = new JsonArray([.. given.Select(g => (JsonNode)g)]) }),
            }.ToJsonString());
}

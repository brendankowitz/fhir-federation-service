using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The seed of the ordinary-count backend: three synthetic Patients and nothing else, so no
/// reference-witness resource is in this database. The expected totals live in the tests as literals.
/// </summary>
internal static class CountSeedData
{
    public static readonly BackendSeed Seed = new(
        "Patient",
        ["count-female-1", "count-female-2", "count-male-1"],
        _ =>
        [
            Patient("count-female-1", "female"),
            Patient("count-female-2", "female"),
            Patient("count-male-1", "male"),
        ]);

    private static SeedResource Patient(string id, string gender) =>
        new("Patient", id, new JsonObject { ["resourceType"] = "Patient", ["id"] = id, ["gender"] = gender }.ToJsonString());
}

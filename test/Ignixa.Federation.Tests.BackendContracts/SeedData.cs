using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The seeded resources. All ids are reserved synthetic ids: a failure report may print them, and
/// anything else a backend returns is described without its id.
/// </summary>
internal static class SeedData
{
    public const string DuplicatePatientId = "dup";
    public const string OtherPatientId = "other";

    public const string RelativeObservationId = "obs-relative-dup";
    public const string OwnAbsoluteObservationId = "obs-own-absolute-dup";
    public const string ForeignAbsoluteObservationId = "obs-foreign-absolute-dup";
    public const string ControlObservationId = "obs-control-other";

    /// <summary>
    /// A syntactically absolute http(s) base that no query or seed step ever contacts. FHIR search
    /// compares reference text only, and the backend never resolves it.
    /// </summary>
    public const string ForeignBaseUrl = "https://foreign-shard.invalid/";

    private static readonly string[] ObservationIds =
    [
        RelativeObservationId,
        OwnAbsoluteObservationId,
        ForeignAbsoluteObservationId,
        ControlObservationId,
    ];

    /// <summary>Seed ids in the order they are written. Patients come first so local references resolve.</summary>
    public static IReadOnlyList<string> AllObservationIds => ObservationIds;

    public static bool IsReservedObservationId(string id) => Array.IndexOf(ObservationIds, id) >= 0;

    /// <summary>
    /// The resources to write, in write order. <paramref name="localBaseUrl"/> is the base the test
    /// client uses against the backend, with a trailing slash.
    /// </summary>
    public static IReadOnlyList<SeedResource> Resources(string localBaseUrl) =>
    [
        Patient(DuplicatePatientId),
        Patient(OtherPatientId),
        Observation(RelativeObservationId, $"Patient/{DuplicatePatientId}"),
        Observation(OwnAbsoluteObservationId, $"{localBaseUrl}Patient/{DuplicatePatientId}"),
        Observation(ForeignAbsoluteObservationId, $"{ForeignBaseUrl}Patient/{DuplicatePatientId}"),
        Observation(ControlObservationId, $"Patient/{OtherPatientId}"),
    ];

    private static SeedResource Patient(string id) =>
        new("Patient", id, new JsonObject { ["resourceType"] = "Patient", ["id"] = id }.ToJsonString());

    private static SeedResource Observation(string id, string subjectReference) =>
        new(
            "Observation",
            id,
            new JsonObject
            {
                ["resourceType"] = "Observation",
                ["id"] = id,
                ["status"] = "final",
                ["code"] = new JsonObject { ["text"] = "backend-contract" },
                ["subject"] = new JsonObject { ["reference"] = subjectReference },
            }.ToJsonString());
}

internal sealed record SeedResource(string ResourceType, string Id, string Json);

using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.UnitTests.Capabilities;
using Ignixa.Federation.Shards.UnitTests.Http;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>Scripted loopback answers for the strict-handling probe: the advertisement, the positive control and the challenge.</summary>
internal static class ProbeScript
{
    /// <summary>The first parameter code the probe tries; the probe must move off it when the shard advertises it.</summary>
    public const string Code = "ignixa-strict-probe";

    /// <summary>A server statement whose resources hold the given <c>(type, codes)</c> pairs and whose common parameters are <paramref name="common"/>.</summary>
    public static ScriptedResponse Advertisement(string[]? common = null, params (string Type, string[] Codes)[] resources)
    {
        string[] searchable = [.. resources.Select(r => CapabilityJson.SearchableResource(
            r.Type,
            $",\"searchParam\":{CapabilityJson.Arr([.. r.Codes.Select(c => CapabilityJson.Param(c, "string"))])}"))];
        return ScriptedResponse.Json(CapabilityJson.ServerStatement(
            CapabilityJson.Arr(searchable),
            CapabilityJson.Arr([.. (common ?? []).Select(c => CapabilityJson.Param(c, "string"))])));
    }

    /// <summary>An advertisement of <c>Patient</c> with a single <c>name</c> parameter.</summary>
    public static ScriptedResponse PatientAdvertisement() => Advertisement(null, ("Patient", ["name"]));

    /// <summary>A searchset that passes the self check for the bare <c>_count=1</c> control.</summary>
    public static ScriptedResponse Control(ShardHarness shard, string? next = null) =>
        ScriptedResponse.Json(Searchsets.Page(shard.Search + "?_count=1", next));

    /// <summary>A searchset that passes the self check for the challenge: it echoes the unadvertised parameter, as a shard that ignored strictness might.</summary>
    public static ScriptedResponse EchoedChallenge(ShardHarness shard, string code = Code) =>
        ScriptedResponse.Json(Searchsets.Page(shard.Search + $"?{code}=1&_count=1"));

    /// <summary>A searchset answering the challenge while dropping the parameter from its <c>self</c> link.</summary>
    public static ScriptedResponse DroppedChallenge(ShardHarness shard) =>
        ScriptedResponse.Json(Searchsets.Page(shard.Search + "?_count=1"));

    /// <summary>The request as the server saw it: method and target, plus the form body for a POST.</summary>
    public static string Wire(RecordedRequest request) =>
        request.Method == "POST"
            ? $"POST {request.Target} {System.Text.Encoding.ASCII.GetString(request.Body)}"
            : $"{request.Method} {request.Target}";

    /// <summary>Scripts a shard that advertises <c>Patient</c>, passes the control, and answers the challenge with <paramref name="challenge"/>.</summary>
    public static void RespondWithChallenge(ShardHarness shard, ScriptedResponse challenge) =>
        shard.Server.Respond(PatientAdvertisement(), Control(shard), challenge);

    /// <summary>The expected exchange of the control search for <paramref name="method"/>.</summary>
    public static string ControlWire(ShardSearchMethod method) =>
        method == ShardSearchMethod.PostSearch ? "POST /fhir/Patient/_search _count=1" : "GET /fhir/Patient?_count=1";

    /// <summary>The expected exchange of the challenge search for <paramref name="method"/> and parameter <paramref name="code"/>.</summary>
    public static string ChallengeWire(ShardSearchMethod method, string code = Code) =>
        method == ShardSearchMethod.PostSearch ? $"POST /fhir/Patient/_search {code}=1&_count=1" : $"GET /fhir/Patient?{code}=1&_count=1";
}

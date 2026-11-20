using System.Collections.Concurrent;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.UnitTests.Http;

namespace Ignixa.Federation.Shards.UnitTests.Probes;

/// <summary>
/// Answers a <see cref="LoopbackFhirServer"/> by request, not by queue position: the host's capability refresher and the
/// startup probes send concurrently, so only the wire request decides the answer. Each wire has its own queue of answers;
/// the advertisement is repeatable. A request nobody scripted gets the server's 599.
/// </summary>
internal sealed class ProbeRoutes
{
    public const string Metadata = "GET /fhir/metadata";

    private const string Sort = "_sort=_lastUpdated&_count=1";
    private const string Early = "2026-01-02T03:00:00Z";
    private const string Late = "2026-01-02T03:30:00Z";

    private readonly ConcurrentDictionary<string, ConcurrentQueue<ScriptedResponse>> _queues = new();
    private readonly string _baseUrl;

    public ProbeRoutes(LoopbackFhirServer server)
    {
        _baseUrl = server.BaseUrl;
        server.Router = Answer;
    }

    /// <summary>Gets or sets the answer to every metadata request; by default a statement advertising <c>Patient</c> search.</summary>
    public ScriptedResponse Advertisement { get; set; } = ProbeScript.PatientAdvertisement();

    public static string ControlWire(string type, ShardSearchMethod method) =>
        method == ShardSearchMethod.PostSearch ? $"POST /fhir/{type}/_search _count=1" : $"GET /fhir/{type}?_count=1";

    public static string ChallengeWire(string type, ShardSearchMethod method) =>
        method == ShardSearchMethod.PostSearch
            ? $"POST /fhir/{type}/_search {ProbeScript.Code}=1&_count=1"
            : $"GET /fhir/{type}?{ProbeScript.Code}=1&_count=1";

    public static string StartWire(string type, ShardSearchMethod method) =>
        method == ShardSearchMethod.PostSearch ? $"POST /fhir/{type}/_search {Sort}" : $"GET /fhir/{type}?{Sort}";

    /// <summary>Gets the wire that redeems the cursor the first page of a <paramref name="method"/> stream carries.</summary>
    public static string RedeemWire(string type, ShardSearchMethod method, NextLinkRedemption redemption) =>
        method == ShardSearchMethod.PostSearch && redemption == NextLinkRedemption.RepostToSearch
            ? $"POST /fhir/{type}/_search {Sort}&ct={Token(method)}"
            : $"GET /fhir/{type}?{Sort}&ct={Token(method)}";

    /// <summary>Scripts the positive control and an HTTP 400 for the unadvertised parameter, unless a test supplies another answer.</summary>
    public void Strict(string type, ShardSearchMethod method, ScriptedResponse? control = null, ScriptedResponse? challenge = null)
    {
        On(ControlWire(type, method), control ?? ScriptedResponse.Json(Searchsets.Page($"{_baseUrl}/{type}?_count=1")));
        On(ChallengeWire(type, method), challenge ?? ScriptedResponse.Status(400));
    }

    /// <summary>Scripts a first page, then two redemptions of its cursor, each a different resource from the first page's.</summary>
    public void Replay(
        string type,
        ShardSearchMethod method,
        NextLinkRedemption redemption,
        ScriptedResponse? first = null,
        ScriptedResponse? firstRedemption = null,
        ScriptedResponse? secondRedemption = null)
    {
        On(StartWire(type, method), first ?? FirstPage(type, method));
        string redeem = RedeemWire(type, method, redemption);
        On(redeem, firstRedemption ?? Redeemed(type, "b1"), secondRedemption ?? Redeemed(type, "b1"));
    }

    /// <summary>Scripts a shard that proves strict handling and shows the replay for <paramref name="method"/>.</summary>
    public void Succeed(string type, ShardSearchMethod method, NextLinkRedemption redemption)
    {
        Strict(type, method);
        Replay(type, method, redemption);
    }

    public ScriptedResponse FirstPage(string type, ShardSearchMethod method, bool withNext = true) =>
        ScriptedResponse.Json(Searchsets.Page(
            $"{_baseUrl}/{type}?{Sort}",
            withNext ? $"{_baseUrl}/{type}?{Sort}&ct={Token(method)}" : null,
            null,
            Searchsets.Entry(Resource(type, "a1", Early))));

    public static ScriptedResponse Redeemed(string type, string id) =>
        ScriptedResponse.Json(Searchsets.Page(null, null, null, Searchsets.Entry(Resource(type, id, Late))));

    public void On(string wire, params ScriptedResponse[] responses)
    {
        ConcurrentQueue<ScriptedResponse> queue = _queues.GetOrAdd(wire, _ => new());
        foreach (ScriptedResponse response in responses)
        {
            queue.Enqueue(response);
        }
    }

    private static string Token(ShardSearchMethod method) => method == ShardSearchMethod.PostSearch ? "P" : "G";

    private static string Resource(string type, string id, string at) =>
        $$$"""{"resourceType":"{{{type}}}","id":"{{{id}}}","meta":{"versionId":"1","lastUpdated":"{{{at}}}"}}""";

    private ScriptedResponse? Answer(RecordedRequest request)
    {
        string wire = ProbeScript.Wire(request);
        if (wire == Metadata)
        {
            return Advertisement;
        }

        return _queues.TryGetValue(wire, out ConcurrentQueue<ScriptedResponse>? queue) && queue.TryDequeue(out ScriptedResponse? response)
            ? response
            : null;
    }
}

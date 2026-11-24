using System.Globalization;
using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>One reserved Patient version as the scripted backend serves it. <see cref="Instant"/> is the direct-read spelling.</summary>
internal sealed record FakeRow(string Id, string Version, string Instant)
{
    /// <summary>The same point in time spelled as UTC with a <c>Z</c>, as a search page spells it.</summary>
    public string UtcInstant => DateTimeOffset.Parse(Instant, CultureInfo.InvariantCulture).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";
}

/// <summary>What the scripted backend is about to answer for one sorted request, for a test to replace.</summary>
/// <param name="Position">The zero-based position of the row the page holds. Three is the terminal page.</param>
/// <param name="Redemption">How many times this exact query was served before this request.</param>
/// <param name="Served">The rows in the order this request's sort selects.</param>
/// <param name="DefaultNext">The next link the backend would issue, or <see langword="null"/>.</param>
internal sealed record ContinuationVisit(int Position, int Redemption, IReadOnlyList<FakeRow> Served, string? DefaultNext);

/// <summary>
/// A loopback stand-in for the Microsoft FHIR Server answering only direct Patient reads and sorted,
/// <c>_count=1</c> searches of three Patients. It accepts a request only in the exact wire shape the
/// harness must send (the configured method, <c>Patient</c> or <c>Patient/_search</c>) and answers 400
/// otherwise, so a wrong method or target fails instead of being tolerated. It records every request.
/// </summary>
internal sealed class ScriptedPatientBackend : IAsyncDisposable
{
    public static readonly FakeRow Oldest = new("count-male-1", "1", "2026-01-01T10:00:00.1000000+00:00");
    public static readonly FakeRow Middle = new("count-female-1", "1", "2026-01-01T10:00:01.2000000Z");
    public static readonly FakeRow Newest = new("count-female-2", "2", "2026-01-01T05:00:02.3000000-05:00");

    private readonly LoopbackFhirServer _server = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _served;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _visits = [];
    private readonly List<CapturedRequest> _requests = [];
    private int _serial;

    public ScriptedPatientBackend(HttpMethod expectedMethod)
    {
        ExpectedMethod = expectedMethod;
        Direct = new Dictionary<string, FakeRow>(StringComparer.Ordinal)
        {
            [Oldest.Id] = Oldest,
            [Middle.Id] = Middle,
            [Newest.Id] = Newest,
        };
        _served = _server.ServeAsync(Handle, _stop.Token);
    }

    public Uri BaseAddress => _server.BaseAddress;

    public HttpMethod ExpectedMethod { get; }

    /// <summary>What a direct read of each id returns. A test may replace entries before the walk.</summary>
    public Dictionary<string, FakeRow> Direct { get; }

    /// <summary>Issue a different token for every request, so each redemption leads to its own successor.</summary>
    public bool UniqueTokens { get; init; }

    /// <summary>After the third row, offer a next link to an empty terminal page instead of omitting it.</summary>
    public bool EmptyTerminal { get; init; }

    /// <summary>Let the empty terminal page itself offer a next link.</summary>
    public bool TerminalNext { get; init; }

    /// <summary>Always serve ascending, whatever the request asks.</summary>
    public bool IgnoreSort { get; init; }

    /// <summary>Replaces the whole response body of a sorted request when it returns non-null.</summary>
    public Func<ContinuationVisit, string?>? Override { get; init; }

    /// <summary>Replaces the issued next link.</summary>
    public Func<string, string>? RewriteNext { get; init; }

    /// <summary>Stops serving and returns every request received, in arrival order.</summary>
    public async Task<IReadOnlyList<CapturedRequest>> StopAsync()
    {
        await _stop.CancelAsync();
        await _served;
        lock (_gate)
        {
            return [.. _requests];
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _served;
        _server.Dispose();
        _stop.Dispose();
    }

    public static string Page(IEnumerable<FakeRow> rows, string? next, string mode = "match", string resourceType = "Patient", bool rawInstant = false)
    {
        var entries = new JsonArray();
        foreach (FakeRow row in rows)
        {
            entries.Add(new JsonObject { ["resource"] = Resource(row, rawInstant ? row.Instant : row.UtcInstant, resourceType), ["search"] = new JsonObject { ["mode"] = mode } });
        }

        var links = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = "http://127.0.0.1:1/Patient?_count=1" });
        if (next is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = next });
        }

        return new JsonObject { ["resourceType"] = "Bundle", ["type"] = "searchset", ["entry"] = entries, ["link"] = links }.ToJsonString();
    }

    public static JsonObject Resource(FakeRow row, string instant, string resourceType = "Patient") => new()
    {
        ["resourceType"] = resourceType,
        ["id"] = row.Id,
        ["meta"] = new JsonObject { ["versionId"] = row.Version, ["lastUpdated"] = instant },
    };

    private (int Status, string Body) Handle(CapturedRequest request)
    {
        lock (_gate)
        {
            _requests.Add(request);
            string[] line = request.RequestLine.Split(' ');
            string method = line[0];
            string target = line[1];

            if (method == "GET" && target.StartsWith("/Patient/", StringComparison.Ordinal))
            {
                return Direct.TryGetValue(target["/Patient/".Length..], out FakeRow? row)
                    ? (200, Resource(row, row.Instant).ToJsonString())
                    : (404, """{"resourceType":"OperationOutcome"}""");
            }

            string? query = (method, target) switch
            {
                ("GET", _) when target.StartsWith("/Patient?", StringComparison.Ordinal) => target["/Patient?".Length..],
                ("POST", "/Patient/_search") => request.Body,
                _ => null,
            };
            if (query is null || method != ExpectedMethod.Method
                || !request.Headers.Contains("Prefer: handling=strict", StringComparison.Ordinal))
            {
                return Reject();
            }

            Dictionary<string, string> pairs = [];
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0 || !pairs.TryAdd(pair[..eq], pair[(eq + 1)..]))
                {
                    return Reject();
                }
            }

            if (!pairs.TryGetValue("_sort", out string? sort) || sort is not ("_lastUpdated" or "-_lastUpdated")
                || pairs.GetValueOrDefault("_count") != "1")
            {
                return Reject();
            }

            int position = pairs.TryGetValue("ct", out string? token) ? int.Parse(token.Split('-')[0][1..], CultureInfo.InvariantCulture) : 0;
            FakeRow[] served = Order(sort);
            string? next = position switch
            {
                < 2 => Token(sort, position + 1),
                2 when EmptyTerminal => Token(sort, 3),
                3 when TerminalNext => Token(sort, 4),
                _ => null,
            };
            if (next is not null && RewriteNext is not null)
            {
                next = RewriteNext(next);
            }

            int redemption = _visits.GetValueOrDefault(query);
            _visits[query] = redemption + 1;
            var visit = new ContinuationVisit(position, redemption, served, next);
            return (200, Override?.Invoke(visit) ?? Page(position < 3 ? [served[position]] : [], next));
        }

        static (int, string) Reject() => (400, """{"resourceType":"OperationOutcome"}""");
    }

    private string Token(string sort, int position) =>
        $"{BaseAddress.GetLeftPart(UriPartial.Authority)}/Patient?_sort={sort}&_count=1&ct=P{position}{(UniqueTokens ? $"-{++_serial}" : string.Empty)}";

    private FakeRow[] Order(string sort)
    {
        FakeRow[] ascending = [Oldest, Middle, Newest];
        return IgnoreSort || sort[0] != '-' ? ascending : [.. ascending.Reverse()];
    }
}

using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the Patient.given evidence path; they run without Docker. A case passes only
/// when two independent facts hold: the match multiset equals the literal expectation, and <c>Bundle.self</c>
/// echoes exactly the decoded selection pairs. The controls here show that each fact fails alone while the other
/// is correct, so neither can excuse the other. The canned bundles stand in for what a backend might return,
/// and every wrong multiset and wrong echo below is a literal, never computed from the seed or a response.
/// </summary>
public sealed class PatientGivenEvidenceTests
{
    private const string UntrustedUrl = "https://do-not-log-this-url.invalid/Patient?_getpages=secret";
    private const string UnreservedId = "do-not-log-this-id";
    private const string UnreservedValue = "do-not-log-this-value";

    [Fact]
    public void The_given_seed_ids_match_its_resources_and_are_disjoint_from_the_count_seed()
    {
        IReadOnlyList<SeedResource> resources = GivenSeedData.Seed.Resources("http://127.0.0.1:45123/");

        Assert.Equal("Patient", GivenSeedData.Seed.ResourceType);
        Assert.All(resources, r => Assert.Equal("Patient", r.ResourceType));
        Assert.Equal(
            [.. resources.Select(r => r.Id).Order(StringComparer.Ordinal)],
            [.. GivenSeedData.Seed.Ids.Order(StringComparer.Ordinal)]);
        Assert.All(GivenSeedData.Seed.Ids, id => Assert.True(GivenSeedData.IsReservedId(id)));
        Assert.False(GivenSeedData.IsReservedId(UnreservedId));
        Assert.DoesNotContain(CountSeedData.Seed.Ids, id => GivenSeedData.IsReservedId(id));
        Assert.All(GivenCases.All.SelectMany(c => c.ExpectedIds), id => Assert.Contains(id, GivenSeedData.Seed.Ids));
    }

    [Fact]
    public async Task Readiness_for_the_given_seed_uses_a_positive_id_page_of_all_six_patients()
    {
        string? query = null;
        string[] ids = [.. GivenSeedData.Seed.Ids];

        await MicrosoftFhirSqlBackend.WaitForSeedSearchableAsync(
            (q, _) =>
            {
                query = q;
                return Task.FromResult(SearchsetEvidence.Parse(Bundle(ids, selfQuery: null), "Patient"));
            },
            GivenSeedData.Seed.ResourceType,
            GivenSeedData.Seed.Ids,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("_id=given-both,given-case,given-left,given-long,given-neither,given-right", query);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("exact")]
    [InlineData("exact-upper")]
    public void The_literal_matches_with_the_exact_echo_are_the_expected_result(string label)
    {
        GivenCase given = GivenCases.For(label);

        GivenEvidence evidence = GivenEvidence.Parse(CorrectBundle(given));

        Assert.True(evidence.MatchesIds(given));
        Assert.True(evidence.EchoesSelection(given));
        Assert.True(evidence.IsAsExpected(given));
        string report = evidence.Describe($"GET {label}", given);
        Assert.StartsWith($"[contract] GET {label}: matched.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: matched", report, StringComparison.Ordinal);
    }

    // A correct echo never excuses a wrong match. Each row is a plausible wrong semantics for its case.
    [Theory]
    [InlineData("and", "given-both,given-case,given-left,given-long")] // only the first conjunct applied
    [InlineData("and", "given-both,given-right")] // only the second conjunct applied
    [InlineData("or", "given-both,given-case,given-left,given-long")] // the Mica branch lost
    [InlineData("or", "given-both,given-right")] // the Quartz branch lost
    [InlineData("or", "given-both")] // AND answered for OR
    [InlineData("exact", "given-both,given-case,given-left,given-long")] // :exact ignored: prefix and case-insensitive
    [InlineData("exact-upper", "given-both,given-case,given-left")] // :exact matched case-insensitively
    [InlineData("exact-upper", "")] // the exact-case Patient rejected
    [InlineData("left", "given-both,given-left,given-left")] // a duplicated match
    [InlineData("right", "given-both")] // a partial page whose total still agrees
    public void A_wrong_match_multiset_fails_even_with_a_correct_echo(string label, string wrongIds)
    {
        GivenCase given = GivenCases.For(label);
        ArgumentNullException.ThrowIfNull(wrongIds);

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(SplitIds(wrongIds), CorrectSelf(given)));

        Assert.True(evidence.EchoesSelection(given));
        Assert.False(evidence.MatchesIds(given));
        Assert.False(evidence.IsAsExpected(given));
        string report = evidence.Describe($"POST {label}", given);
        Assert.StartsWith($"[contract] POST {label}: unsupported match behavior.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: matched", report, StringComparison.Ordinal);
    }

    // A correct multiset never excuses a dropped, replaced, duplicated, added or merged predicate.
    [Theory]
    [InlineData("and", "given=Quartz")] // second given dropped
    [InlineData("and", "given=Mica")] // first given dropped
    [InlineData("and", "given=Quartz&given=Quartz")] // second replaced by a duplicate of the first
    [InlineData("and", "given=Quartz%2CMica")] // AND merged into an OR value
    [InlineData("and", "given=Quartz&given=Mica&given=Mica")] // a duplicated pair added
    [InlineData("and", "given=Quartz&given=Mica&gender=female")] // an unexpected parameter added
    [InlineData("and", "")] // nothing echoed
    [InlineData("or", "given=Quartz&given=Mica")] // OR split into AND
    [InlineData("or", "given=Quartz")] // one OR branch lost
    [InlineData("exact", "given=Quartz")] // the :exact modifier dropped
    [InlineData("exact", "given%3Aexact=quartz")] // the value replaced by another case
    [InlineData("exact-upper", "given%3Aexact=Quartz")] // the value replaced
    [InlineData("exact-upper", "given=QUARTZ")] // the modifier dropped
    public void A_wrong_echo_fails_even_with_the_correct_match_multiset(string label, string selfSelection)
    {
        GivenCase given = GivenCases.For(label);
        ArgumentNullException.ThrowIfNull(selfSelection);

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(given.ExpectedIds, WithControls(selfSelection)));

        Assert.True(evidence.MatchesIds(given));
        Assert.False(evidence.EchoesSelection(given));
        Assert.False(evidence.IsAsExpected(given));
        string report = evidence.Describe($"GET {label}", given);
        Assert.StartsWith($"[contract] GET {label}: matched.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: unsupported echo behavior", report, StringComparison.Ordinal);
    }

    // Order, spelling and the page controls do not matter; only the decoded pairs and their multiplicity do.
    [Theory]
    [InlineData("and", "given=Mica&given=Quartz")]
    [InlineData("and", "given=Quartz&%67iven=%4Dica")]
    [InlineData("and", "given=Quartz&given=Mica&_count=100&_total=accurate")]
    [InlineData("or", "given=Quartz,Mica")]
    [InlineData("or", "given=Quartz%2cMica")]
    [InlineData("exact", "given:exact=Quartz")]
    [InlineData("exact", "given%3aexact=%51uartz&_total=accurate&_count=100")]
    public void The_echo_may_spell_the_same_decoded_selection_in_any_order_with_or_without_controls(string label, string selfQuery)
    {
        GivenCase given = GivenCases.For(label);

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(given.ExpectedIds, selfQuery));

        Assert.True(evidence.IsAsExpected(given), selfQuery);
    }

    [Fact]
    public void A_report_names_missing_pairs_and_only_counts_unexpected_ones()
    {
        GivenCase and = GivenCases.For("and");

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(and.ExpectedIds, WithControls("given=Quartz&given=Quartz")));

        string report = evidence.Describe("GET and", and);
        Assert.Contains("missing: [given=Mica]", report, StringComparison.Ordinal);
        Assert.Contains("unexpected: 1 pair(s)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_never_hold_response_text_or_unreserved_ids_and_values()
    {
        GivenCase and = GivenCases.For("and");
        GivenEvidence evidence = GivenEvidence.Parse(Bundle(
            [UnreservedId, "given-both"],
            $"{UntrustedUrl}&given=Quartz&{UnreservedValue}=1&given={UnreservedValue}&_count=100"));

        string report = evidence.Describe("GET and", and);

        Assert.DoesNotContain(UnreservedId, report, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedValue, report, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-url", report, StringComparison.Ordinal);
        Assert.DoesNotContain("_getpages", report, StringComparison.Ordinal);
        Assert.Contains("<unreserved id>", report, StringComparison.Ordinal);
        Assert.False(evidence.IsAsExpected(and));
    }

    public static TheoryData<string, string> NotEvidenceBodies()
    {
        GivenCase left = GivenCases.For("left");
        string good = CorrectBundle(left);
        var noTotal = (JsonObject)JsonNode.Parse(good)!;
        noTotal.Remove("total");

        return new TheoryData<string, string>
        {
            { "next link", Bundle(left.ExpectedIds, CorrectSelf(left), nextUrl: UntrustedUrl) },
            { "total above the entries", Bundle(left.ExpectedIds, CorrectSelf(left), total: 5) },
            { "total below the entries", Bundle(left.ExpectedIds, CorrectSelf(left), total: 3) },
            { "total zero with entries", Bundle(left.ExpectedIds, CorrectSelf(left), total: 0) },
            { "no total", noTotal.ToJsonString() },
            { "entry of another type", good.Replace("\"resourceType\":\"Patient\",\"id\":\"given-left\"", "\"resourceType\":\"Observation\",\"id\":\"given-left\"", StringComparison.Ordinal) },
            { "include entry", good.Replace("\"mode\":\"match\"", "\"mode\":\"include\"", StringComparison.Ordinal) },
            { "entry without an id", good.Replace("\"id\":\"given-left\"", "\"id\":\"\"", StringComparison.Ordinal) },
            { "not json", "not json" },
            { "operation outcome", "{\"resourceType\":\"OperationOutcome\"}" },
            { "collection bundle", "{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"total\":0}" },
            { "no self link", Bundle(left.ExpectedIds, selfQuery: null) },
            { "two self links", Bundle(left.ExpectedIds, CorrectSelf(left), extraSelfQuery: CorrectSelf(left)) },
            { "malformed self query", Bundle(left.ExpectedIds, "given=Quartz%") },
            { "self link without a url", good.Replace("\"link\":[{", "\"link\":[{\"relation\":\"self\"},{", StringComparison.Ordinal) },
            { "duplicate json member", good.Replace("\"type\":\"searchset\"", "\"type\":\"searchset\",\"link\":[]", StringComparison.Ordinal) },
        };
    }

    [Theory]
    [MemberData(nameof(NotEvidenceBodies))]
    public void A_response_that_cannot_be_accounted_for_is_not_evidence_and_is_not_echoed(string name, string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => GivenEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
        Assert.True(!failure.Message.Contains(body, StringComparison.Ordinal), name);
    }

    // The real client puts the literal escapes on the wire: GET in the target and POST in the raw form body.
    [Theory]
    [InlineData("GET", "and", "given=Quartz&given=Mica")]
    [InlineData("POST", "and", "given=Quartz&given=Mica")]
    [InlineData("GET", "or", "given=Quartz%2CMica")]
    [InlineData("POST", "or", "given=Quartz%2CMica")]
    [InlineData("GET", "exact", "given%3Aexact=Quartz")]
    [InlineData("POST", "exact", "given%3Aexact=Quartz")]
    public async Task The_real_client_puts_the_literal_escapes_on_the_wire_with_strict_handling(string method, string label, string wire)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GivenCase given = GivenCases.For(label);
        string expectedLine = method == "GET" ? $"GET /Patient?{wire}&_count=100&_total=accurate HTTP/1.1" : "POST /Patient/_search HTTP/1.1";
        string expectedBody = method == "GET" ? string.Empty : $"{wire}&_count=100&_total=accurate";
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync(CorrectBundle(given), cancellationToken);
        using var client = new BackendHttpClient(server.BaseAddress);

        GivenEvidence evidence = await client.SearchGivenAsync(new HttpMethod(method), given, cancellationToken);

        CapturedRequest request = await served;
        Assert.Equal(expectedLine, request.RequestLine);
        Assert.Equal(expectedBody, request.Body);
        Assert.Contains("Prefer: handling=strict", request.Headers, StringComparison.Ordinal);
        Assert.Equal(method == "POST", request.Headers.Contains("Content-Type: application/x-www-form-urlencoded", StringComparison.Ordinal));
        Assert.True(evidence.IsAsExpected(given));
    }

    [Theory]
    [InlineData(400, BackendFailureKind.UnsupportedCapability, "POST exact-upper", "HTTP 400")]
    [InlineData(200, BackendFailureKind.IncompleteSearch, "POST exact-upper", "not evidence")]
    public async Task A_rejected_or_incomplete_answer_names_the_method_and_case_and_is_not_a_result(
        int status,
        BackendFailureKind kind,
        string named,
        string reason)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GivenCase given = GivenCases.For("exact-upper");
        string body = status == 200
            ? Bundle(given.ExpectedIds, CorrectSelf(given), nextUrl: UntrustedUrl)
            : "{\"resourceType\":\"OperationOutcome\"}";
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync(body, cancellationToken, status);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchGivenAsync(HttpMethod.Post, given, cancellationToken));
        await served;

        Assert.Equal(kind, failure.Kind);
        Assert.Contains(named, failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationOutcome", failure.Message, StringComparison.Ordinal);
    }

    private static string[] SplitIds(string ids) => ids.Length == 0 ? [] : ids.Split(',');

    private static string WithControls(string selection) =>
        selection.Length == 0 ? "_count=100&_total=accurate" : $"{selection}&_count=100&_total=accurate";

    private static string Encode(IEnumerable<(string Name, string Value)> pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    private static string CorrectSelf(GivenCase given) =>
        Encode([.. given.ExpectedSelection, ("_count", "100"), ("_total", "accurate")]);

    private static string CorrectBundle(GivenCase given) => Bundle(given.ExpectedIds, CorrectSelf(given));

    private static string Bundle(
        IEnumerable<string> ids,
        string? selfQuery,
        int? total = null,
        string? extraSelfQuery = null,
        string? nextUrl = null)
    {
        var links = new JsonArray();
        foreach (string query in new[] { selfQuery, extraSelfQuery }.OfType<string>())
        {
            links.Add(new JsonObject { ["relation"] = "self", ["url"] = $"http://127.0.0.1:1/Patient?{query}" });
        }

        if (nextUrl is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = nextUrl });
        }

        var entries = new JsonArray();
        int count = 0;
        foreach (string id in ids)
        {
            count++;
            entries.Add(new JsonObject
            {
                ["resource"] = new JsonObject { ["resourceType"] = "Patient", ["id"] = id },
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = total ?? count,
            ["link"] = links,
            ["entry"] = entries,
        }.ToJsonString();
    }
}

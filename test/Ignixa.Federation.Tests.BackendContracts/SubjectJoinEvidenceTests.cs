using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the Observation.subject join evidence path; they run without Docker. A case
/// passes only when two independent facts hold: the match multiset equals the literal expectation, and
/// <c>Bundle.self</c> echoes the decoded <c>subject</c> terms with their multiplicity. Each control shows that one
/// fact fails while the other is correct. The canned bundles stand in for what a backend might return; the shared
/// searchset parser has its own tests, so only representative unaccountable pages are repeated here. An echo is what
/// the backend reported, not evidence that a term filtered the result, and a form POST echo of one or two equal
/// occurrences is characterized, not authorized.
/// </summary>
public sealed class SubjectJoinEvidenceTests
{
    private const string LocalBase = "http://127.0.0.1:45123/custom/r4/";
    private const string UntrustedUrl = "https://do-not-log-this-url.invalid/Observation?_getpages=secret";
    private const string UnreservedId = "do-not-log-this-id";
    private const string UnreservedValue = "do-not-log-this-value";

    private const string ForeignId = "obs-foreign-absolute-dup";
    private const string ForeignBId = "obs-foreign-b-absolute-dup";
    private const string OwnId = "obs-own-absolute-dup";
    private const string RelativeId = "obs-relative-dup";

    private static readonly HttpMethod[] BothMethods = [HttpMethod.Get, HttpMethod.Post];

    public static TheoryData<string, string> MethodsAndLabels()
    {
        var data = new TheoryData<string, string>();
        foreach (HttpMethod method in BothMethods)
        {
            foreach (string label in SubjectJoinCases.Labels)
            {
                data.Add(method.Method, label);
            }
        }

        return data;
    }

    [Fact]
    public void The_cases_send_the_seeded_references_and_equal_sends_two_raw_spellings_of_one_decoded_term()
    {
        Dictionary<string, string> subjects = SeedData.Resources(LocalBase)
            .Select(r => JsonNode.Parse(r.Json)!)
            .Where(json => json["resourceType"]!.GetValue<string>() == "Observation")
            .ToDictionary(json => json["id"]!.GetValue<string>(), json => json["subject"]!["reference"]!.GetValue<string>());
        string foreign = subjects[ForeignId];
        string own = subjects[OwnId];

        Assert.Equal([foreign], Case("foreign-single").ExpectedTerms.Select(t => t.Value));
        Assert.Equal([own], Case("own-single").ExpectedTerms.Select(t => t.Value));
        Assert.Equal([foreign, own], Case("unequal").ExpectedTerms.Select(t => t.Value));
        Assert.NotEqual(foreign, own);

        SubjectJoinCase equal = Case("equal");
        Assert.Equal([foreign, foreign], equal.ExpectedTerms.Select(t => t.Value));
        string[] raw = equal.WireQuery.Split('&');
        Assert.Equal(2, raw.Length);
        Assert.NotEqual(raw[0], raw[1]);
        Assert.StartsWith("%73ubject=", raw[1], StringComparison.Ordinal);
        Assert.Contains("%50atient", raw[1], StringComparison.Ordinal);
        Assert.Equal([foreign, foreign], raw.Select(p => Uri.UnescapeDataString(p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..])));
        Assert.Equal(["subject", "subject"], raw.Select(p => Uri.UnescapeDataString(p[..p.IndexOf('=', StringComparison.Ordinal)])));
    }

    // A correct echo never excuses a wrong match. Each row is a plausible wrong semantics for its case.
    [Theory]
    [InlineData("equal", "")] // nothing selected although foreign A matches each occurrence alone
    [InlineData("equal", $"{ForeignId},{ForeignBId}")] // the host ignored
    [InlineData("unequal", $"{ForeignId}")] // the foreign term alone selected something
    [InlineData("unequal", $"{OwnId},{RelativeId}")] // the own term alone selected something
    [InlineData("unequal", $"{ForeignId},{OwnId},{RelativeId}")] // OR answered for AND
    [InlineData("foreign-single", "")] // the foreign reference rejected
    [InlineData("own-single", $"{OwnId}")] // the relative reference excluded
    public void A_wrong_match_multiset_fails_even_with_the_exact_echo(string label, string wrongIds)
    {
        ArgumentNullException.ThrowIfNull(wrongIds);
        SubjectJoinCase join = Case(label);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(wrongIds.Length == 0 ? [] : wrongIds.Split(','), CorrectSelf(join)));

        foreach (HttpMethod method in BothMethods)
        {
            Assert.Equal(SubjectEchoOutcome.Echoed, evidence.Echo(join, method));
            Assert.False(evidence.IsAsExpected(join, method));
            Assert.StartsWith($"[contract] {method.Method} {label}: unsupported match behavior.", evidence.Describe($"{method.Method} {label}", join, method), StringComparison.Ordinal);
        }
    }

    // A correct match multiset never excuses a dropped, replaced, added, tripled or changed term, for either method.
    [Theory]
    [InlineData("equal", "")] // nothing echoed
    [InlineData("equal", "foreign-a,foreign-a,foreign-a")] // tripled
    [InlineData("equal", "foreign-a,own")] // the second occurrence replaced
    [InlineData("equal", "foreign-a,foreign-a,other")] // an unrelated subject added
    [InlineData("equal", "foreign-a,modified-name")] // the second name changed to subject:Patient
    [InlineData("unequal", "foreign-a")] // the own term dropped
    [InlineData("unequal", "foreign-a,foreign-a")] // the own term replaced by a duplicate of foreign
    [InlineData("unequal", "foreign-a,foreign-b")] // the own term altered to another host
    [InlineData("unequal", "foreign-a,own,gender")] // an unrelated parameter added
    [InlineData("foreign-single", "foreign-a,foreign-a")] // doubled
    [InlineData("own-single", "foreign-a")] // replaced
    public void A_wrong_echo_fails_even_with_the_correct_match_multiset(string label, string echoedTerms)
    {
        ArgumentNullException.ThrowIfNull(echoedTerms);
        SubjectJoinCase join = Case(label);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(join.ExpectedIds, SelfFor(echoedTerms)));

        foreach (HttpMethod method in BothMethods)
        {
            Assert.True(evidence.MatchesIds(join));
            Assert.Equal(SubjectEchoOutcome.Rejected, evidence.Echo(join, method));
            Assert.False(evidence.IsAsExpected(join, method));
            string report = evidence.Describe($"{method.Method} {label}", join, method);
            Assert.StartsWith($"[contract] {method.Method} {label}: matched.", report, StringComparison.Ordinal);
            Assert.Contains("echo of decoded selection pairs: unsupported echo behavior", report, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Two_equal_occurrences_must_echo_twice_for_GET_while_one_or_two_in_a_form_POST_are_only_characterized()
    {
        SubjectJoinCase equal = Case("equal");
        SubjectJoinEvidence two = SubjectJoinEvidence.Parse(Bundle(equal.ExpectedIds, SelfFor("foreign-a,foreign-a")));
        SubjectJoinEvidence one = SubjectJoinEvidence.Parse(Bundle(equal.ExpectedIds, SelfFor("foreign-a")));

        Assert.True(two.IsAsExpected(equal, HttpMethod.Get));
        Assert.Contains("echo of decoded selection pairs: matched", two.Describe("GET equal", equal, HttpMethod.Get), StringComparison.Ordinal);
        Assert.False(one.IsAsExpected(equal, HttpMethod.Get));
        Assert.Contains("unsupported echo behavior (observed: foreign-a x1, other x0; expected: foreign-a x2)", one.Describe("GET equal", equal, HttpMethod.Get), StringComparison.Ordinal);

        Assert.Equal(SubjectEchoOutcome.Echoed, two.Echo(equal, HttpMethod.Post));
        Assert.True(two.IsAsExpected(equal, HttpMethod.Post));
        string twoReport = two.Describe("POST equal", equal, HttpMethod.Post);
        Assert.StartsWith("[contract] POST equal: matched.", twoReport, StringComparison.Ordinal);
        Assert.Contains("characterization only: every occurrence echoed (foreign-a x2, other x0)", twoReport, StringComparison.Ordinal);

        Assert.Equal(SubjectEchoOutcome.Collapsed, one.Echo(equal, HttpMethod.Post));
        Assert.True(one.IsAsExpected(equal, HttpMethod.Post));
        string oneReport = one.Describe("POST equal", equal, HttpMethod.Post);
        Assert.StartsWith("[contract] POST equal: matched.", oneReport, StringComparison.Ordinal);
        Assert.Contains("characterization only: collapsed to one occurrence per term (foreign-a x1, other x0)", oneReport, StringComparison.Ordinal);

        // Distinct terms are never collapsible, and a characterized echo never excuses a wrong result.
        SubjectJoinCase unequal = Case("unequal");
        SubjectJoinEvidence onlyForeign = SubjectJoinEvidence.Parse(Bundle(unequal.ExpectedIds, SelfFor("foreign-a")));
        Assert.False(onlyForeign.IsAsExpected(unequal, HttpMethod.Post));
        Assert.DoesNotContain("characterization", CorrectEvidence(unequal).Describe("POST unequal", unequal, HttpMethod.Post), StringComparison.Ordinal);
        foreach (string tokens in new[] { "foreign-a,foreign-a", "foreign-a" })
        {
            Assert.False(SubjectJoinEvidence.Parse(Bundle([], SelfFor(tokens))).IsAsExpected(equal, HttpMethod.Post));
        }
    }

    // Order, spelling and the page controls do not matter; only the decoded pairs and their multiplicity do.
    [Theory]
    [InlineData("equal", "alt-name-foreign&_count=100&plain-foreign&_total=accurate")]
    [InlineData("equal", "lower-hex-foreign&alt-name-foreign")]
    [InlineData("unequal", "plain-own&plain-foreign")]
    [InlineData("unequal", "_total=accurate&alt-name-own&_count=100&lower-hex-foreign")]
    public void The_echo_may_spell_the_same_decoded_selection_in_any_order_with_or_without_controls(string label, string spellings)
    {
        ArgumentNullException.ThrowIfNull(spellings);
        SubjectJoinCase join = Case(label);
        string foreign = Uri.EscapeDataString(Case("foreign-single").ExpectedTerms[0].Value);
        string own = Uri.EscapeDataString(Case("own-single").ExpectedTerms[0].Value);
        string selfQuery = spellings
            .Replace("plain-foreign", $"subject={foreign}", StringComparison.Ordinal)
            .Replace("alt-name-foreign", $"%73ubject={foreign}", StringComparison.Ordinal)
            .Replace("lower-hex-foreign", $"subject={foreign.Replace("%3A", "%3a", StringComparison.Ordinal).Replace("%2F", "%2f", StringComparison.Ordinal)}", StringComparison.Ordinal)
            .Replace("plain-own", $"subject={own}", StringComparison.Ordinal)
            .Replace("alt-name-own", $"%73ubject={own}", StringComparison.Ordinal);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(join.ExpectedIds, selfQuery));

        Assert.Equal(SubjectEchoOutcome.Echoed, evidence.Echo(join, HttpMethod.Get));
    }

    [Fact]
    public void Reports_never_hold_response_text_reference_values_or_unreserved_ids()
    {
        SubjectJoinCase unequal = Case("unequal");
        string foreign = unequal.ExpectedTerms[0].Value;
        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(
            [UnreservedId, ForeignId],
            $"{UntrustedUrl.Split('?')[1]}&subject={Uri.EscapeDataString(foreign)}&{UnreservedValue}=1&subject={UnreservedValue}&_count=100"));

        string report = evidence.Describe("POST unequal", unequal, HttpMethod.Post);

        Assert.DoesNotContain(UnreservedId, report, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedValue, report, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-url", report, StringComparison.Ordinal);
        Assert.DoesNotContain("_getpages", report, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", report, StringComparison.Ordinal);
        Assert.DoesNotContain("foreign-shard", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Patient/dup", report, StringComparison.Ordinal);
        Assert.Contains("<unreserved id>", report, StringComparison.Ordinal);
        Assert.Contains("observed: foreign-a x1, own x0, other x3", report, StringComparison.Ordinal);
        Assert.False(evidence.IsAsExpected(unequal, HttpMethod.Post));
    }

    // Only the answers this class adds on top of the shared searchset parser: a partial page and a self link it cannot read.
    [Theory]
    [InlineData("next link")]
    [InlineData("entry of another type")]
    [InlineData("no self link")]
    [InlineData("malformed self query")]
    public void A_response_that_cannot_be_accounted_for_is_not_evidence_and_is_not_echoed(string name)
    {
        SubjectJoinCase equal = Case("equal");
        string body = name switch
        {
            "next link" => Bundle(equal.ExpectedIds, CorrectSelf(equal), nextUrl: UntrustedUrl),
            "entry of another type" => CorrectBundle(equal).Replace("\"resourceType\":\"Observation\"", "\"resourceType\":\"Patient\"", StringComparison.Ordinal),
            "no self link" => Bundle(equal.ExpectedIds, selfQuery: null),
            _ => Bundle(equal.ExpectedIds, "subject=%"),
        };

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SubjectJoinEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(body, failure.Message, StringComparison.Ordinal);
    }

    // The real client puts the literal spellings on the wire: GET in the target and POST in the raw form body.
    [Theory]
    [MemberData(nameof(MethodsAndLabels))]
    public async Task The_real_client_puts_the_literal_spellings_on_the_loopback_wire_with_strict_handling(string method, string label)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        SubjectJoinCase join = SubjectJoinCases.For(label, server.BaseAddress.AbsoluteUri);
        string expectedLine = method == "GET"
            ? $"GET /Observation?{join.WireQuery}&_count=100&_total=accurate HTTP/1.1"
            : "POST /Observation/_search HTTP/1.1";
        string expectedBody = method == "GET" ? string.Empty : $"{join.WireQuery}&_count=100&_total=accurate";
        Task<CapturedRequest> served = server.ServeOnceAsync(CorrectBundle(join), cancellationToken);
        using var client = new BackendHttpClient(server.BaseAddress);

        SubjectJoinEvidence evidence = await client.SearchSubjectJoinAsync(new HttpMethod(method), join, cancellationToken);

        CapturedRequest request = await served;
        Assert.Equal(expectedLine, request.RequestLine);
        Assert.Equal(expectedBody, request.Body);
        Assert.Contains("Prefer: handling=strict", request.Headers, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{server.BaseAddress.Port}", request.Headers, StringComparison.Ordinal);
        Assert.Equal(method == "POST", request.Headers.Contains("Content-Type: application/x-www-form-urlencoded", StringComparison.Ordinal));
        Assert.True(evidence.IsAsExpected(join, new HttpMethod(method)));
    }

    [Fact]
    public async Task A_rejected_search_names_the_method_and_case_and_is_not_a_result()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        SubjectJoinCase join = SubjectJoinCases.For("unequal", server.BaseAddress.AbsoluteUri);
        Task<CapturedRequest> served = server.ServeOnceAsync("{\"resourceType\":\"OperationOutcome\"}", cancellationToken, 400);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchSubjectJoinAsync(HttpMethod.Post, join, cancellationToken));
        await served;

        Assert.Equal(BackendFailureKind.UnsupportedCapability, failure.Kind);
        Assert.Contains("POST unequal Observation.subject", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationOutcome", failure.Message, StringComparison.Ordinal);
    }

    private static SubjectJoinCase Case(string label) => SubjectJoinCases.For(label, LocalBase);

    private static string Pair(string name, string value) => $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

    /// <summary>A self query for the comma-separated echo tokens, always followed by the two page controls.</summary>
    private static string SelfFor(string tokens)
    {
        string foreign = Case("foreign-single").ExpectedTerms[0].Value;
        string own = Case("own-single").ExpectedTerms[0].Value;
        string foreignB = new Uri(new UriBuilder(LocalBase) { Host = "foreign-shard-b.invalid" }.Uri, "Patient/dup").AbsoluteUri;
        IEnumerable<string> pairs = tokens.Length == 0 ? [] : tokens.Split(',').Select(token => token switch
        {
            "foreign-a" => Pair("subject", foreign),
            "own" => Pair("subject", own),
            "foreign-b" => Pair("subject", foreignB),
            "other" => Pair("subject", "Patient/other"),
            "gender" => Pair("gender", "female"),
            "modified-name" => Pair("subject:Patient", foreign),
            _ => throw new ArgumentOutOfRangeException(nameof(tokens), "Unknown echo token."),
        });
        return string.Join('&', [.. pairs, "_count=100", "_total=accurate"]);
    }

    private static string CorrectSelf(SubjectJoinCase join) =>
        string.Join('&', [.. join.ExpectedTerms.Select(t => Pair("subject", t.Value)), "_count=100", "_total=accurate"]);

    private static string CorrectBundle(SubjectJoinCase join) => Bundle(join.ExpectedIds, CorrectSelf(join));

    private static SubjectJoinEvidence CorrectEvidence(SubjectJoinCase join) => SubjectJoinEvidence.Parse(CorrectBundle(join));

    private static string Bundle(IEnumerable<string> ids, string? selfQuery, string? nextUrl = null)
    {
        var links = new JsonArray();
        if (selfQuery is not null)
        {
            links.Add(new JsonObject { ["relation"] = "self", ["url"] = $"http://127.0.0.1:1/Observation?{selfQuery}" });
        }

        if (nextUrl is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = nextUrl });
        }

        var entries = new JsonArray();
        foreach (string id in ids)
        {
            entries.Add(new JsonObject
            {
                ["resource"] = new JsonObject { ["resourceType"] = "Observation", ["id"] = id },
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = entries.Count,
            ["link"] = links,
        };

        // An empty searchset has no entry member in FHIR JSON.
        if (entries.Count > 0)
        {
            bundle["entry"] = entries;
        }

        return bundle.ToJsonString();
    }
}

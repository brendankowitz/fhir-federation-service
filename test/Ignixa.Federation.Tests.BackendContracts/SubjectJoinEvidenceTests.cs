using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the Observation.subject join evidence path; they run without Docker. A case
/// passes only when two independent facts hold: the match multiset equals the literal expectation, and
/// <c>Bundle.self</c> echoes exactly the decoded <c>subject</c> terms with their multiplicity. Each control shows
/// that one fact fails alone while the other is correct. The canned bundles stand in for what a backend might
/// return, and every wrong multiset and wrong echo below is a literal, never computed from a response.
/// An echo is what the backend reported. It is not evidence that a term filtered the result, and a form POST that
/// echoes or collapses equal occurrences is characterized here, not authorized for production joins.
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

    private static readonly string[] Methods = ["GET", "POST"];

    private static SubjectJoinCase Case(string label) => SubjectJoinCases.For(label, LocalBase);

    private static HttpMethod Method(string method) => new(method);

    public static TheoryData<string, string> MethodsAndLabels()
    {
        var data = new TheoryData<string, string>();
        foreach (string method in Methods)
        {
            foreach (string label in SubjectJoinCases.Labels)
            {
                data.Add(method, label);
            }
        }

        return data;
    }

    [Fact]
    public void The_case_terms_are_the_exact_seeded_subject_references_of_the_same_patient_id()
    {
        Dictionary<string, string> subjects = SeedData.Resources(LocalBase)
            .Select(r => JsonNode.Parse(r.Json)!)
            .Where(json => json["resourceType"]!.GetValue<string>() == "Observation")
            .ToDictionary(json => json["id"]!.GetValue<string>(), json => json["subject"]!["reference"]!.GetValue<string>());

        string foreign = subjects[ForeignId];
        string own = subjects[OwnId];
        Assert.Equal([foreign], Case("foreign-single").ExpectedTerms.Select(t => t.Value));
        Assert.Equal([own], Case("own-single").ExpectedTerms.Select(t => t.Value));
        Assert.Equal([foreign, foreign], Case("equal").ExpectedTerms.Select(t => t.Value));
        Assert.Equal([foreign, own], Case("unequal").ExpectedTerms.Select(t => t.Value));

        // The unequal terms name the same Patient/dup on two different bases, so only the base differs.
        Assert.NotEqual(foreign, own);
        Assert.EndsWith("/Patient/dup", foreign, StringComparison.Ordinal);
        Assert.EndsWith("/Patient/dup", own, StringComparison.Ordinal);
        Assert.Equal("foreign-shard.invalid", new Uri(foreign).Host);
        Assert.Equal("127.0.0.1", new Uri(own).Host);
        Assert.NotEqual(subjects[ForeignBId], foreign);

        Assert.All(SubjectJoinCases.Labels.SelectMany(l => Case(l).ExpectedIds), id => Assert.True(SeedData.IsReservedObservationId(id)));
    }

    [Fact]
    public void The_equal_case_sends_two_raw_spellings_of_one_decoded_term()
    {
        SubjectJoinCase equal = Case("equal");

        string[] raw = equal.WireQuery.Split('&');
        Assert.Equal(2, raw.Length);
        Assert.NotEqual(raw[0], raw[1]);
        Assert.StartsWith("subject=", raw[0], StringComparison.Ordinal);
        Assert.StartsWith("%73ubject=", raw[1], StringComparison.Ordinal);
        Assert.Contains("%50atient", raw[1], StringComparison.Ordinal);
        Assert.DoesNotContain("%50atient", raw[0], StringComparison.Ordinal);

        (string Name, string Value)[] decoded = [.. raw.Select(p => (Uri.UnescapeDataString(p[..p.IndexOf('=', StringComparison.Ordinal)]), Uri.UnescapeDataString(p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..])))];
        Assert.Equal(decoded[0], decoded[1]);
        Assert.Equal("subject", decoded[0].Name);
        Assert.Equal(equal.ExpectedTerms[0].Value, decoded[0].Value);
    }

    [Fact]
    public void The_unequal_case_sends_foreign_a_and_the_own_absolute_reference()
    {
        SubjectJoinCase unequal = Case("unequal");

        string[] raw = unequal.WireQuery.Split('&');
        Assert.Equal(2, raw.Length);
        Assert.All(raw, p => Assert.StartsWith("subject=", p, StringComparison.Ordinal));
        Assert.Equal(
            [unequal.ExpectedTerms[0].Value, unequal.ExpectedTerms[1].Value],
            raw.Select(p => Uri.UnescapeDataString(p["subject=".Length..])));
    }

    [Theory]
    [MemberData(nameof(MethodsAndLabels))]
    public void The_literal_matches_with_the_exact_echo_are_the_expected_result(string method, string label)
    {
        SubjectJoinCase join = Case(label);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(CorrectBundle(join));

        Assert.True(evidence.MatchesIds(join));
        Assert.Equal(SubjectEchoOutcome.Echoed, evidence.Echo(join, Method(method)));
        Assert.True(evidence.IsAsExpected(join, Method(method)));
        string report = evidence.Describe($"{method} {label}", join, Method(method));
        Assert.StartsWith($"[contract] {method} {label}: matched.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: matched", report, StringComparison.Ordinal);
    }

    // A correct echo never excuses a wrong match. Each row is a plausible wrong semantics for its case.
    [Theory]
    [InlineData("equal", "")] // nothing selected although foreign A matches each occurrence alone
    [InlineData("equal", $"{ForeignId},{ForeignId}")] // the single reference counted once per occurrence
    [InlineData("equal", $"{ForeignId},{ForeignBId}")] // the host ignored
    [InlineData("equal", $"{ForeignId},{OwnId},{RelativeId}")] // the own references joined in
    [InlineData("unequal", $"{ForeignId}")] // the foreign term alone selected something
    [InlineData("unequal", $"{OwnId},{RelativeId}")] // the own term alone selected something
    [InlineData("unequal", $"{ForeignId},{OwnId},{RelativeId}")] // OR answered for AND
    [InlineData("foreign-single", "")] // the foreign reference rejected
    [InlineData("foreign-single", $"{ForeignId},{ForeignBId}")] // the host ignored
    [InlineData("own-single", $"{OwnId}")] // the relative reference excluded
    [InlineData("own-single", $"{OwnId},{RelativeId},{ForeignId}")] // a foreign reference admitted
    public void A_wrong_match_multiset_fails_even_with_a_correct_echo(string label, string wrongIds)
    {
        SubjectJoinCase join = Case(label);
        ArgumentNullException.ThrowIfNull(wrongIds);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(SplitIds(wrongIds), CorrectSelf(join)));

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            Assert.Equal(SubjectEchoOutcome.Echoed, evidence.Echo(join, method));
            Assert.False(evidence.MatchesIds(join));
            Assert.False(evidence.IsAsExpected(join, method));
            string report = evidence.Describe($"{method.Method} {label}", join, method);
            Assert.StartsWith($"[contract] {method.Method} {label}: unsupported match behavior.", report, StringComparison.Ordinal);
            Assert.Contains("echo of decoded selection pairs: matched", report, StringComparison.Ordinal);
        }
    }

    // A correct multiset never excuses a dropped, replaced, added, tripled or merged term, for either method.
    [Theory]
    [InlineData("equal", "")] // nothing echoed
    [InlineData("equal", "foreign-a,foreign-a,foreign-a")] // tripled
    [InlineData("equal", "foreign-a,own")] // the second occurrence replaced by another term
    [InlineData("equal", "own,own")] // replaced
    [InlineData("equal", "foreign-a,foreign-b")] // the second occurrence altered to another host
    [InlineData("equal", "foreign-a,foreign-a,other")] // an unrelated subject added
    [InlineData("equal", "foreign-a,foreign-a,gender")] // an unrelated parameter added
    [InlineData("equal", "foreign-a,modified-name")] // the second name changed to subject:Patient
    [InlineData("unequal", "")] // nothing echoed
    [InlineData("unequal", "foreign-a")] // the own term dropped
    [InlineData("unequal", "own")] // the foreign term dropped
    [InlineData("unequal", "foreign-a,foreign-a")] // the own term replaced by a duplicate of foreign
    [InlineData("unequal", "own,own")] // the foreign term replaced by a duplicate of own
    [InlineData("unequal", "foreign-a,own,own")] // a duplicate added
    [InlineData("unequal", "foreign-a,foreign-a,own")] // a duplicate added
    [InlineData("unequal", "foreign-a,own,other")] // an unrelated subject added
    [InlineData("unequal", "foreign-a,foreign-b")] // the own term altered to another host
    [InlineData("unequal", "foreign-a,own,gender")] // an unrelated parameter added
    [InlineData("unequal", "foreign-a,modified-name")] // a name changed
    [InlineData("foreign-single", "")] // nothing echoed
    [InlineData("foreign-single", "own")] // replaced
    [InlineData("foreign-single", "foreign-a,foreign-a")] // doubled
    [InlineData("foreign-single", "foreign-a,other")] // an unrelated subject added
    [InlineData("own-single", "foreign-a")] // replaced
    [InlineData("own-single", "own,gender")] // an unrelated parameter added
    public void A_wrong_echo_fails_even_with_the_correct_match_multiset(string label, string echoedTerms)
    {
        SubjectJoinCase join = Case(label);
        ArgumentNullException.ThrowIfNull(echoedTerms);

        SubjectJoinEvidence evidence = SubjectJoinEvidence.Parse(Bundle(join.ExpectedIds, SelfFor(echoedTerms)));

        foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post })
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
    public void A_collapsed_equal_pair_fails_for_GET_even_with_the_correct_result_and_is_characterized_for_POST()
    {
        SubjectJoinCase equal = Case("equal");
        SubjectJoinEvidence collapsed = SubjectJoinEvidence.Parse(Bundle(equal.ExpectedIds, SelfFor("foreign-a")));

        Assert.True(collapsed.MatchesIds(equal));
        Assert.Equal(SubjectEchoOutcome.Rejected, collapsed.Echo(equal, HttpMethod.Get));
        Assert.False(collapsed.IsAsExpected(equal, HttpMethod.Get));
        Assert.Contains("unsupported echo behavior (observed: foreign-a x1, other x0; expected: foreign-a x2)", collapsed.Describe("GET equal", equal, HttpMethod.Get), StringComparison.Ordinal);

        Assert.Equal(SubjectEchoOutcome.Collapsed, collapsed.Echo(equal, HttpMethod.Post));
        Assert.True(collapsed.IsAsExpected(equal, HttpMethod.Post));
        string report = collapsed.Describe("POST equal", equal, HttpMethod.Post);
        Assert.StartsWith("[contract] POST equal: matched.", report, StringComparison.Ordinal);
        Assert.Contains("collapsed to one occurrence per term (characterization only; foreign-a x1, other x0)", report, StringComparison.Ordinal);

        // The characterization never excuses a wrong result.
        SubjectJoinEvidence collapsedWrongResult = SubjectJoinEvidence.Parse(Bundle([], SelfFor("foreign-a")));
        Assert.False(collapsedWrongResult.IsAsExpected(equal, HttpMethod.Post));
    }

    [Fact]
    public void Distinct_unequal_terms_must_each_be_echoed_once_for_either_method()
    {
        SubjectJoinCase unequal = Case("unequal");
        SubjectJoinEvidence onlyForeign = SubjectJoinEvidence.Parse(Bundle(unequal.ExpectedIds, SelfFor("foreign-a")));
        SubjectJoinEvidence both = SubjectJoinEvidence.Parse(Bundle(unequal.ExpectedIds, SelfFor("foreign-a,own")));

        Assert.False(onlyForeign.IsAsExpected(unequal, HttpMethod.Post));
        Assert.Equal(SubjectEchoOutcome.Echoed, both.Echo(unequal, HttpMethod.Post));
        Assert.Equal(SubjectEchoOutcome.Echoed, both.Echo(unequal, HttpMethod.Get));
    }

    // Order, spelling and the page controls do not matter; only the decoded pairs and their multiplicity do.
    [Theory]
    [InlineData("equal", "plain-foreign&alt-name-foreign")]
    [InlineData("equal", "alt-name-foreign&_count=100&plain-foreign&_total=accurate")]
    [InlineData("equal", "lower-hex-foreign&alt-letter-foreign")]
    [InlineData("unequal", "plain-foreign&plain-own")]
    [InlineData("unequal", "plain-own&plain-foreign")]
    [InlineData("unequal", "_total=accurate&alt-name-own&_count=100&lower-hex-foreign")]
    public void The_echo_may_spell_the_same_decoded_selection_in_any_order_with_or_without_controls(string label, string spellings)
    {
        SubjectJoinCase join = Case(label);
        ArgumentNullException.ThrowIfNull(spellings);
        string foreign = join.ExpectedTerms.First(t => t.Label == "foreign-a").Value;
        string own = Case("own-single").ExpectedTerms[0].Value;
        string selfQuery = spellings
            .Replace("plain-foreign", $"subject={Uri.EscapeDataString(foreign)}", StringComparison.Ordinal)
            .Replace("alt-name-foreign", $"%73ubject={Uri.EscapeDataString(foreign)}", StringComparison.Ordinal)
            .Replace("lower-hex-foreign", $"subject={Uri.EscapeDataString(foreign).Replace("%3A", "%3a", StringComparison.Ordinal).Replace("%2F", "%2f", StringComparison.Ordinal)}", StringComparison.Ordinal)
            .Replace("alt-letter-foreign", $"s%75bject={Uri.EscapeDataString(foreign)}", StringComparison.Ordinal)
            .Replace("plain-own", $"subject={Uri.EscapeDataString(own)}", StringComparison.Ordinal)
            .Replace("alt-name-own", $"%73ubject={Uri.EscapeDataString(own)}", StringComparison.Ordinal);

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

    public static TheoryData<string, string> NotEvidenceBodies()
    {
        SubjectJoinCase equal = Case("equal");
        string good = CorrectBundle(equal);
        var noTotal = (JsonObject)JsonNode.Parse(good)!;
        noTotal.Remove("total");

        return new TheoryData<string, string>
        {
            { "next link", Bundle(equal.ExpectedIds, CorrectSelf(equal), nextUrl: UntrustedUrl) },
            { "total above the entries", Bundle(equal.ExpectedIds, CorrectSelf(equal), total: 5) },
            { "total below the entries", Bundle(equal.ExpectedIds, CorrectSelf(equal), total: 0) },
            { "no total", noTotal.ToJsonString() },
            { "entry of another type", good.Replace("\"resourceType\":\"Observation\",\"id\":\"obs-foreign-absolute-dup\"", "\"resourceType\":\"Patient\",\"id\":\"obs-foreign-absolute-dup\"", StringComparison.Ordinal) },
            { "include entry", good.Replace("\"mode\":\"match\"", "\"mode\":\"include\"", StringComparison.Ordinal) },
            { "entry without an id", good.Replace("\"id\":\"obs-foreign-absolute-dup\"", "\"id\":\"\"", StringComparison.Ordinal) },
            { "not json", "not json" },
            { "operation outcome", "{\"resourceType\":\"OperationOutcome\"}" },
            { "collection bundle", "{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"total\":0}" },
            { "no self link", Bundle(equal.ExpectedIds, selfQuery: null) },
            { "two self links", Bundle(equal.ExpectedIds, CorrectSelf(equal), extraSelfQuery: CorrectSelf(equal)) },
            { "malformed self query", Bundle(equal.ExpectedIds, "subject=%") },
            { "self link without a url", good.Replace("\"link\":[{", "\"link\":[{\"relation\":\"self\"},{", StringComparison.Ordinal) },
            { "duplicate json member", good.Replace("\"type\":\"searchset\"", "\"type\":\"searchset\",\"link\":[]", StringComparison.Ordinal) },
        };
    }

    [Theory]
    [MemberData(nameof(NotEvidenceBodies))]
    public void A_response_that_cannot_be_accounted_for_is_not_evidence_and_is_not_echoed(string name, string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SubjectJoinEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
        Assert.True(!failure.Message.Contains(body, StringComparison.Ordinal), name);
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

        SubjectJoinEvidence evidence = await client.SearchSubjectJoinAsync(Method(method), join, cancellationToken);

        CapturedRequest request = await served;
        Assert.Equal(expectedLine, request.RequestLine);
        Assert.Equal(expectedBody, request.Body);
        Assert.Contains("Prefer: handling=strict", request.Headers, StringComparison.Ordinal);
        Assert.Contains($"Host: 127.0.0.1:{server.BaseAddress.Port}", request.Headers, StringComparison.Ordinal);
        Assert.Equal(method == "POST", request.Headers.Contains("Content-Type: application/x-www-form-urlencoded", StringComparison.Ordinal));
        if (label == "equal")
        {
            string wire = method == "GET" ? request.RequestLine : request.Body;
            Assert.Contains("&%73ubject=", wire, StringComparison.Ordinal);
            Assert.Contains("%50atient", wire, StringComparison.Ordinal);
        }

        Assert.True(evidence.IsAsExpected(join, Method(method)));
    }

    [Theory]
    [InlineData(400, BackendFailureKind.UnsupportedCapability, "POST unequal Observation.subject", "HTTP 400")]
    [InlineData(200, BackendFailureKind.IncompleteSearch, "POST unequal Observation.subject", "not evidence")]
    public async Task A_rejected_or_incomplete_answer_names_the_method_and_case_and_is_not_a_result(
        int status,
        BackendFailureKind kind,
        string named,
        string reason)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        SubjectJoinCase join = SubjectJoinCases.For("unequal", server.BaseAddress.AbsoluteUri);
        string body = status == 200
            ? Bundle(join.ExpectedIds, CorrectSelf(join), nextUrl: UntrustedUrl)
            : "{\"resourceType\":\"OperationOutcome\"}";
        Task<CapturedRequest> served = server.ServeOnceAsync(body, cancellationToken, status);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchSubjectJoinAsync(HttpMethod.Post, join, cancellationToken));
        await served;

        Assert.Equal(kind, failure.Kind);
        Assert.Contains(named, failure.Message, StringComparison.Ordinal);
        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationOutcome", failure.Message, StringComparison.Ordinal);
    }

    private static string[] SplitIds(string ids) => ids.Length == 0 ? [] : ids.Split(',');

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
            links.Add(new JsonObject { ["relation"] = "self", ["url"] = $"http://127.0.0.1:1/Observation?{query}" });
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
                ["resource"] = new JsonObject { ["resourceType"] = "Observation", ["id"] = id },
                ["search"] = new JsonObject { ["mode"] = "match" },
            });
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = total ?? count,
            ["link"] = links,
        };

        // An empty searchset has no entry member in FHIR JSON.
        if (count > 0)
        {
            bundle["entry"] = entries;
        }

        return bundle.ToJsonString();
    }
}

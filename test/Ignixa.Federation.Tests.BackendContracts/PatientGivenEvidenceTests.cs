using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the Patient.given evidence path. They run without Docker and show that:
/// the six-case matrix, its wire text and its literal expectations are pinned; the real client puts the
/// literal escapes on the wire for GET and form POST; the parser accounts for a complete page and for every
/// decoded selection pair of <c>Bundle.self</c> with its multiplicity; a plausible wrong match multiset fails
/// even with a correct echo; a correct multiset fails when the echo drops, alters, adds or collapses a
/// predicate; and neither a truncated, partial, duplicated, mis-typed nor falsely complete page is a result.
/// The canned bundles stand in for what a backend might return. The expected multisets in this file are
/// literals, never computed from the seed or from a response.
/// </summary>
public sealed class PatientGivenEvidenceTests
{
    private const string UntrustedUrl = "https://do-not-log-this-url.invalid/Patient?_getpages=secret";
    private const string UnreservedId = "do-not-log-this-id";
    private const string UnreservedValue = "do-not-log-this-value";

    private static readonly Uri BaseAddress = new("http://127.0.0.1:45123/");

    public static TheoryData<string> Labels() => [.. GivenCases.All.Select(c => c.Label)];

    [Fact]
    public void The_matrix_is_the_six_pinned_cases_with_literal_wire_text_and_expectations()
    {
        (string Label, string Wire, string Ids, string Pairs)[] pinned =
        [
            ("left", "given=Quartz", "given-both,given-case,given-left,given-long", "given=Quartz"),
            ("right", "given=Mica", "given-both,given-right", "given=Mica"),
            ("and", "given=Quartz&given=Mica", "given-both", "given=Quartz;given=Mica"),
            ("or", "given=Quartz%2CMica", "given-both,given-case,given-left,given-long,given-right", "given=Quartz,Mica"),
            ("exact", "given%3Aexact=Quartz", "given-both,given-left", "given:exact=Quartz"),
            ("exact-upper", "given%3Aexact=QUARTZ", "given-case", "given:exact=QUARTZ"),
        ];

        Assert.Equal(pinned.Select(p => p.Label), GivenCases.All.Select(c => c.Label));
        foreach ((string label, string wire, string ids, string pairs) in pinned)
        {
            GivenCase actual = GivenCases.For(label);
            Assert.Equal(wire, actual.WireQuery);
            Assert.Equal(ids.Split(','), actual.ExpectedIds);
            Assert.Equal(pairs.Split(';').Select(p => (p[..p.IndexOf('=', StringComparison.Ordinal)], p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..])), actual.ExpectedSelection);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_literal_expected_selection_is_what_the_wire_text_decodes_to(string label)
    {
        GivenCase given = GivenCases.For(label);

        string[] decoded = [.. given.WireQuery.Split('&').Select(p => Decode(p[..p.IndexOf('=', StringComparison.Ordinal)]) + "\n" + Decode(p[(p.IndexOf('=', StringComparison.Ordinal) + 1)..]))];

        Assert.Equal(
            [.. given.ExpectedSelection.Select(p => p.Name + "\n" + p.Value).Order(StringComparer.Ordinal)],
            [.. decoded.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public void An_unknown_case_label_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GivenCases.For("neither"));
    }

    [Fact]
    public void The_given_seed_is_six_synthetic_patients_with_literal_given_names()
    {
        IReadOnlyList<SeedResource> resources = GivenSeedData.Seed.Resources("http://127.0.0.1:45123/");

        Assert.All(resources, r => Assert.Equal("Patient", r.ResourceType));
        string[] seeded = [.. resources.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r =>
            r.Id + "=" + string.Join('|', JsonNode.Parse(r.Json)!["name"]!.AsArray().Single()!["given"]!.AsArray().Select(g => g!.GetValue<string>())))];
        Assert.Equal(
            [
                "given-both=Quartz|Mica",
                "given-case=QUARTZ",
                "given-left=Quartz",
                "given-long=Quartzite",
                "given-neither=Topaz",
                "given-right=Mica",
            ],
            seeded);
        Assert.Equal("Patient", GivenSeedData.Seed.ResourceType);
        Assert.Equal(
            [.. resources.Select(r => r.Id).Order(StringComparer.Ordinal)],
            [.. GivenSeedData.Seed.Ids.Order(StringComparer.Ordinal)]);
        Assert.All(GivenSeedData.Seed.Ids, id => Assert.True(GivenSeedData.IsReservedId(id)));
        Assert.False(GivenSeedData.IsReservedId(UnreservedId));
    }

    [Fact]
    public void The_given_seed_does_not_change_the_count_seed()
    {
        Assert.Equal(["count-female-1", "count-female-2", "count-male-1"], CountSeedData.Seed.Ids);
        Assert.DoesNotContain(CountSeedData.Seed.Ids, id => GivenSeedData.IsReservedId(id));
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
    [MemberData(nameof(Labels))]
    public void A_complete_page_with_the_literal_matches_and_the_exact_echo_is_the_expected_result(string label)
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

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_echo_may_use_any_spelling_order_or_control_pairs_of_the_same_decoded_selection(string label)
    {
        GivenCase given = GivenCases.For(label);
        (string, string)[] selection = [.. given.ExpectedSelection];
        string[] selfQueries =
        [
            Encode([.. selection.Reverse(), ("_total", "accurate"), ("_count", "100")]),
            Encode(selection),
            given.WireQuery + "&_count=100",
            string.Join('&', selection.Select(p => Uri.EscapeDataString(p.Item1).Replace("%3A", ":", StringComparison.Ordinal) + "=" + Uri.EscapeDataString(p.Item2))),
        ];

        foreach (string self in selfQueries)
        {
            GivenEvidence evidence = GivenEvidence.Parse(Bundle(given.ExpectedIds, self));
            Assert.True(evidence.IsAsExpected(given), self);
        }
    }

    [Theory]
    // The prefix is ignored (exact-like): the longer value and the wrong case are lost.
    [InlineData("left", "given-both,given-left")]
    // Only the first given value is applied.
    [InlineData("and", "given-both,given-left")]
    // The pair is treated as AND, not OR.
    [InlineData("or", "given-both")]
    // :exact is ignored, so the prefix and case-insensitive matches return.
    [InlineData("exact", "given-both,given-case,given-left,given-long")]
    // :exact is case-insensitive.
    [InlineData("exact-upper", "given-both,given-case,given-left")]
    // A Patient with neither value is returned.
    [InlineData("right", "given-both,given-neither,given-right")]
    // Duplicated matches with the right set of ids.
    [InlineData("and", "given-both,given-both")]
    [InlineData("or", "given-both,given-case,given-left,given-long,given-right,given-right")]
    [InlineData("right", "given-both,given-both,given-right")]
    [InlineData("exact", "given-both,given-left,given-left")]
    // Nothing, or a sibling that is only the neither row.
    [InlineData("exact-upper", "")]
    [InlineData("exact-upper", "given-case,given-neither")]
    [InlineData("and", "given-neither")]
    public void A_plausible_wrong_multiset_fails_the_match_even_with_a_correct_echo(string label, string wrongIds)
    {
        GivenCase given = GivenCases.For(label);
        ArgumentNullException.ThrowIfNull(wrongIds);
        string[] ids = wrongIds.Length == 0 ? [] : wrongIds.Split(',');

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(ids, CorrectSelf(given)));

        Assert.True(evidence.EchoesSelection(given));
        Assert.False(evidence.MatchesIds(given));
        Assert.False(evidence.IsAsExpected(given));
        string report = evidence.Describe($"POST {label}", given);
        Assert.StartsWith($"[contract] POST {label}: unsupported match behavior.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: matched", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_semantics_multiset_still_passes_the_case_it_is_correct_for()
    {
        // The AND wrong-answer is the OR answer for a different question: a backend must not pass AND with it.
        GivenCase and = GivenCases.For("and");
        GivenCase or = GivenCases.For("or");
        string[] orIds = [.. or.ExpectedIds];

        Assert.False(GivenEvidence.Parse(Bundle(orIds, CorrectSelf(and))).IsAsExpected(and));
        Assert.True(GivenEvidence.Parse(Bundle(orIds, CorrectSelf(or))).IsAsExpected(or));
    }

    public static TheoryData<string, string> EchoBreaks()
    {
        var data = new TheoryData<string, string>();
        foreach (GivenCase given in GivenCases.All)
        {
            foreach (string mutation in new[] { "drop-first", "drop-last", "duplicate-first", "duplicate-last", "extra-pair", "alter-first-value", "alter-first-name", "uppercase-last-value" })
            {
                data.Add(given.Label, mutation);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EchoBreaks))]
    public void A_correct_multiset_cannot_pass_a_dropped_altered_added_or_collapsed_predicate(string label, string mutation)
    {
        GivenCase given = GivenCases.For(label);
        List<(string Name, string Value)> echo = [.. given.ExpectedSelection];
        switch (mutation)
        {
            case "drop-first": echo.RemoveAt(0); break;
            case "drop-last": echo.RemoveAt(echo.Count - 1); break;
            case "duplicate-first": echo.Add(echo[0]); break;
            case "duplicate-last": echo.Insert(0, echo[^1]); break;
            case "extra-pair": echo.Add(("gender", "female")); break;
            case "alter-first-value": echo[0] = (echo[0].Name, echo[0].Value + "x"); break;
            case "alter-first-name": echo[0] = ("family", echo[0].Value); break;
            case "uppercase-last-value": echo[^1] = (echo[^1].Name, echo[^1].Value.ToUpperInvariant()); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        // uppercase-last-value changes nothing for a value that is already upper case, so it is not a break there.
        if (mutation == "uppercase-last-value" && echo.SequenceEqual(given.ExpectedSelection))
        {
            return;
        }

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(given.ExpectedIds, Encode([.. echo, ("_count", "100"), ("_total", "accurate")])));

        Assert.True(evidence.MatchesIds(given));
        Assert.False(evidence.EchoesSelection(given));
        Assert.False(evidence.IsAsExpected(given));
        string report = evidence.Describe($"GET {label}", given);
        Assert.StartsWith($"[contract] GET {label}: matched.", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: unsupported echo behavior", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collapsed_and_that_repeats_one_value_is_not_the_two_different_predicates()
    {
        GivenCase and = GivenCases.For("and");

        GivenEvidence collapsed = GivenEvidence.Parse(Bundle(and.ExpectedIds, "given=Quartz&given=Quartz&_count=100"));
        GivenEvidence merged = GivenEvidence.Parse(Bundle(and.ExpectedIds, "given=Quartz,Mica&_count=100"));
        GivenEvidence last = GivenEvidence.Parse(Bundle(and.ExpectedIds, "given=Mica&_count=100"));

        Assert.False(collapsed.EchoesSelection(and));
        Assert.False(merged.EchoesSelection(and));
        Assert.False(last.EchoesSelection(and));
        Assert.Contains("missing: [given=Mica]", collapsed.Describe("GET and", and), StringComparison.Ordinal);
        Assert.Contains("unexpected: 1 pair(s)", collapsed.Describe("GET and", and), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_multiset_and_a_broken_echo_are_each_reported()
    {
        GivenCase and = GivenCases.For("and");

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(["given-both", "given-left"], "given=Quartz&_count=100"));

        Assert.False(evidence.MatchesIds(and));
        Assert.False(evidence.EchoesSelection(and));
        string report = evidence.Describe("POST and", and);
        Assert.StartsWith("[contract] POST and: unsupported match behavior.", report, StringComparison.Ordinal);
        Assert.Contains("missing: []", report, StringComparison.Ordinal);
        Assert.Contains("unexpected: [given-left]", report, StringComparison.Ordinal);
        Assert.Contains("echo of decoded selection pairs: unsupported echo behavior", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_duplicated_match_is_a_different_multiset_and_not_a_complete_set()
    {
        GivenCase right = GivenCases.For("right");

        GivenEvidence evidence = GivenEvidence.Parse(Bundle(["given-both", "given-right", "given-right"], CorrectSelf(right)));

        Assert.False(evidence.MatchesIds(right));
        Assert.Contains("unexpected: [given-right]", evidence.Describe("GET right", right), StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_with_a_next_link_is_not_evidence_even_with_every_expected_match()
    {
        GivenCase left = GivenCases.For("left");

        BackendEvidenceException failure = AssertNotEvidence(Bundle(left.ExpectedIds, CorrectSelf(left), nextUrl: UntrustedUrl));

        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(3)]
    [InlineData(0)]
    public void A_total_that_does_not_equal_the_entries_is_a_partial_or_falsely_complete_page(int total)
    {
        GivenCase left = GivenCases.For("left");

        AssertNotEvidence(Bundle(left.ExpectedIds, CorrectSelf(left), total: total));
    }

    [Fact]
    public void A_page_without_a_total_is_not_evidence()
    {
        GivenCase left = GivenCases.For("left");
        JsonObject bundle = (JsonObject)JsonNode.Parse(CorrectBundle(left))!;
        bundle.Remove("total");

        AssertNotEvidence(bundle.ToJsonString());
    }

    [Fact]
    public void A_mistyped_include_or_idless_entry_is_not_evidence()
    {
        GivenCase left = GivenCases.For("left");
        string good = CorrectBundle(left);

        AssertNotEvidence(good.Replace("\"resourceType\":\"Patient\",\"id\":\"given-left\"", "\"resourceType\":\"Observation\",\"id\":\"given-left\"", StringComparison.Ordinal));
        AssertNotEvidence(good.Replace("\"mode\":\"match\"", "\"mode\":\"include\"", StringComparison.Ordinal));
        AssertNotEvidence(good.Replace("\"id\":\"given-left\"", "\"id\":\"\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"resourceType\":\"OperationOutcome\"}")]
    [InlineData("{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"total\":0}")]
    public void A_response_that_is_not_a_searchset_is_not_evidence(string body)
    {
        BackendEvidenceException failure = AssertNotEvidence(body);

        Assert.DoesNotContain(body, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_self_link_two_self_links_or_a_malformed_self_link_is_not_evidence()
    {
        GivenCase left = GivenCases.For("left");

        AssertNotEvidence(Bundle(left.ExpectedIds, selfQuery: null));
        AssertNotEvidence(Bundle(left.ExpectedIds, CorrectSelf(left), extraSelfQuery: CorrectSelf(left)));
        AssertNotEvidence(Bundle(left.ExpectedIds, "given=Quartz%"));
        AssertNotEvidence(CorrectBundle(left).Replace("\"link\":[{", "\"link\":[{\"relation\":\"self\"},{", StringComparison.Ordinal));
    }

    [Fact]
    public void A_duplicate_json_member_is_not_evidence()
    {
        GivenCase left = GivenCases.For("left");
        string body = CorrectBundle(left).Replace("\"type\":\"searchset\"", "\"type\":\"searchset\",\"link\":[]", StringComparison.Ordinal);

        AssertNotEvidence(body);
    }

    [Fact]
    public void Reports_never_hold_response_text_or_unreserved_ids_and_values()
    {
        GivenCase and = GivenCases.For("and");
        string body = Bundle(
            [UnreservedId, "given-both"],
            $"{UntrustedUrl}&given=Quartz&{UnreservedValue}=1&given={UnreservedValue}&_count=100");

        GivenEvidence evidence = GivenEvidence.Parse(body);

        string report = evidence.Describe("GET and", and);
        Assert.DoesNotContain(UnreservedId, report, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedValue, report, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-url", report, StringComparison.Ordinal);
        Assert.DoesNotContain("_getpages", report, StringComparison.Ordinal);
        Assert.Contains("<unreserved id>", report, StringComparison.Ordinal);
        Assert.False(evidence.IsAsExpected(and));
    }

    [Fact]
    public void Only_get_and_post_have_a_given_request_shape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackendHttpClient.BuildGivenRequest(HttpMethod.Put, GivenCases.For("left"), BaseAddress));
    }

    public static TheoryData<string, string> MethodsAndLabels()
    {
        var data = new TheoryData<string, string>();
        foreach (string method in new[] { "GET", "POST" })
        {
            foreach (GivenCase given in GivenCases.All)
            {
                data.Add(method, given.Label);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MethodsAndLabels))]
    public async Task The_real_client_puts_the_literal_escapes_on_the_wire_and_returns_the_parsed_evidence(string method, string label)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GivenCase given = GivenCases.For(label);
        (string expectedWire, string expectedBody) = method == "GET"
            ? ($"GET /Patient?{ExpectedWire(label)}&_count=100&_total=accurate HTTP/1.1", string.Empty)
            : ("POST /Patient/_search HTTP/1.1", $"{ExpectedWire(label)}&_count=100&_total=accurate");
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync(CorrectBundle(given), cancellationToken);
        using var client = new BackendHttpClient(server.BaseAddress);

        GivenEvidence evidence = await client.SearchGivenAsync(new HttpMethod(method), given, cancellationToken);

        CapturedRequest wire = await served;
        Assert.Equal(expectedWire, wire.RequestLine);
        Assert.Equal(expectedBody, wire.Body);
        Assert.Contains("Prefer: handling=strict", wire.Headers, StringComparison.Ordinal);
        if (method == "POST")
        {
            Assert.Contains("Content-Type: application/x-www-form-urlencoded", wire.Headers, StringComparison.Ordinal);
        }

        Assert.True(evidence.IsAsExpected(given));
    }

    [Fact]
    public async Task A_rejected_given_search_names_the_method_and_case_and_is_not_a_result()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync("{\"resourceType\":\"OperationOutcome\"}", cancellationToken, status: 400);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchGivenAsync(HttpMethod.Post, GivenCases.For("exact-upper"), cancellationToken));
        await served;

        Assert.Equal(BackendFailureKind.UnsupportedCapability, failure.Kind);
        Assert.Contains("POST exact-upper", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationOutcome", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_response_that_is_not_a_complete_page_is_not_evidence_and_is_named()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GivenCase left = GivenCases.For("left");
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync(Bundle(left.ExpectedIds, CorrectSelf(left), nextUrl: UntrustedUrl), cancellationToken);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchGivenAsync(HttpMethod.Get, left, cancellationToken));
        await served;

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.Contains("GET left", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
    }

    private static string ExpectedWire(string label) => label switch
    {
        "left" => "given=Quartz",
        "right" => "given=Mica",
        "and" => "given=Quartz&given=Mica",
        "or" => "given=Quartz%2CMica",
        "exact" => "given%3Aexact=Quartz",
        "exact-upper" => "given%3Aexact=QUARTZ",
        _ => throw new ArgumentOutOfRangeException(nameof(label)),
    };

    private static BackendEvidenceException AssertNotEvidence(string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => GivenEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        return failure;
    }

    private static string Decode(string formComponent) => Uri.UnescapeDataString(formComponent.Replace('+', ' '));

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

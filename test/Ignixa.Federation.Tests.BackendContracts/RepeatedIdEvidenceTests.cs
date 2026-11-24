using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the repeated decoded <c>_id</c> evidence path. They run without Docker and
/// show that: the real client sends the two <c>_id</c> spellings as literal escapes (GET query and form
/// body); the parser counts decoded names and values in <c>Bundle.self</c> without discarding
/// multiplicity and without following the URL; a collapsed pair is a valid negative characterization,
/// never a pass for preserved predicates; and a wrong, missing, extra or ambiguous response is reported as
/// no outcome at all. The canned bundles stand in for what a backend might return.
/// </summary>
public sealed class RepeatedIdEvidenceTests
{
    private const string SeededId = "count-female-1";
    private const string UntrustedUrl = "https://do-not-log-this-url.invalid/Patient?_getpages=secret";
    private const string UnreservedId = "do-not-log-this-id";

    public static TheoryData<RepeatedIdShape, string, RepeatedIdEcho> SelfQueries()
    {
        (string Query, RepeatedIdEcho Echo)[] rows =
        [
            ("_id=count-female-1&_id=count-female-1", RepeatedIdEcho.Echoed),
            ("_id=count-female-1&%5Fid=%63ount-female-1&_count=100", RepeatedIdEcho.Echoed),
            ("%5Fid=%63ount-female-1&_id=count-female-1&_count=100", RepeatedIdEcho.Echoed),
            ("%5F%69d=count%2Dfemale%2D1&%5fid=%63ount-female-1", RepeatedIdEcho.Echoed),
            ("_id=count-female-1&_count=100", RepeatedIdEcho.Collapsed),
            ("%5Fid=%63ount-female-1&_count=100", RepeatedIdEcho.Collapsed),
            ("x_id=other&_id=count-female-1&note=_id%3Dcount-female-2", RepeatedIdEcho.Collapsed),
        ];

        var data = new TheoryData<RepeatedIdShape, string, RepeatedIdEcho>();
        foreach (RepeatedIdShape shape in Enum.GetValues<RepeatedIdShape>())
        {
            foreach ((string query, RepeatedIdEcho echo) in rows)
            {
                data.Add(shape, query, echo);
            }
        }

        return data;
    }

    public static TheoryData<RepeatedIdShape, string> UnaccountableSelfQueries()
    {
        string[] queries =
        [
            "_count=100",
            "_id=count-female-1&_id=count-female-1&%5Fid=%63ount-female-1",
            "_id=count-female-1&_id=count-female-2",
            "_id=count-female-1&%5Fid=%63ount-female-2",
            "_id=count-female-1,count-female-1",
            "_ID=count-female-1",
            "%5Fid=count-female-1%",
            "_id=count-female-1%2",
            "_id=%63ount-female-1%FF",
            "_id=count+female-1",
            "_id=",
            "_id",
        ];

        var data = new TheoryData<RepeatedIdShape, string>();
        foreach (RepeatedIdShape shape in Enum.GetValues<RepeatedIdShape>())
        {
            foreach (string query in queries)
            {
                data.Add(shape, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SelfQueries))]
    public void The_parser_counts_decoded_occurrences_without_following_the_url(RepeatedIdShape shape, string selfQuery, RepeatedIdEcho expected)
    {
        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(Bundle(shape, SelfUrl(selfQuery)), shape, SeededId);

        Assert.True(evidence.ResultAsExpected);
        Assert.Equal(expected, evidence.Echo);
    }

    [Fact]
    public void A_collapsed_pair_with_the_correct_patient_and_total_is_a_negative_characterization_and_not_preservation()
    {
        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(
            Bundle(RepeatedIdShape.Primary, SelfUrl("_id=count-female-1&_count=100")), RepeatedIdShape.Primary, SeededId);

        Assert.True(evidence.ResultAsExpected);
        Assert.Equal(RepeatedIdEcho.Collapsed, evidence.Echo);
        string report = evidence.Describe("GET primary");
        Assert.Contains("[contract] GET primary: matched.", report, StringComparison.Ordinal);
        Assert.Contains("decoded _id occurrences: 1 (collapsed pair)", report, StringComparison.Ordinal);
        Assert.Contains("not proof of preserved predicates", report, StringComparison.Ordinal);
        Assert.DoesNotContain("preserved the pair", report, StringComparison.Ordinal);
    }

    [Fact]
    public void An_echoed_pair_is_reported_without_claiming_that_the_predicates_were_applied_independently()
    {
        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(
            Bundle(RepeatedIdShape.Count, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1&_count=0")), RepeatedIdShape.Count, SeededId);

        Assert.True(evidence.ResultAsExpected);
        Assert.Equal(RepeatedIdEcho.Echoed, evidence.Echo);
        string report = evidence.Describe("POST count");
        Assert.Contains("[contract] POST count: matched.", report, StringComparison.Ordinal);
        Assert.Contains("decoded _id occurrences: 2 (echoed pair)", report, StringComparison.Ordinal);
        Assert.Contains("does not show that the identical predicates independently affected the result", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], 0)]
    [InlineData(new[] { "count-male-1" }, 1)]
    [InlineData(new[] { "count-female-1", "count-female-2" }, 2)]
    [InlineData(new[] { "count-female-1", "count-female-1" }, 2)]
    public void A_primary_that_does_not_return_exactly_the_seeded_patient_has_no_outcome_even_with_an_echoed_pair(string[] ids, int total)
    {
        string body = Bundle(RepeatedIdShape.Primary, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1&_count=100"), ids, total);

        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(body, RepeatedIdShape.Primary, SeededId);

        Assert.False(evidence.ResultAsExpected);
        Assert.Null(evidence.Echo);
        string report = evidence.Describe("GET primary");
        Assert.StartsWith("[contract] GET primary: unsupported match behavior.", report, StringComparison.Ordinal);
        Assert.Contains("No _id echo outcome is reported.", report, StringComparison.Ordinal);
        Assert.DoesNotContain("echoed pair", report, StringComparison.Ordinal);
        Assert.DoesNotContain("collapsed pair", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_count_that_is_not_exactly_one_has_no_outcome_even_with_an_echoed_pair(int total)
    {
        string body = Bundle(RepeatedIdShape.Count, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1&_count=0"), total: total);

        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(body, RepeatedIdShape.Count, SeededId);

        Assert.False(evidence.ResultAsExpected);
        Assert.Null(evidence.Echo);
        Assert.Contains($"actual: total {total}", evidence.Describe("GET count"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnaccountableSelfQueries))]
    public void A_missing_wrong_extra_or_malformed_id_occurrence_is_not_evidence(RepeatedIdShape shape, string selfQuery)
    {
        string body = Bundle(shape, SelfUrl(selfQuery));

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => RepeatedIdEvidence.Parse(body, shape, SeededId));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain(SelfUrl(selfQuery), failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary)]
    [InlineData(RepeatedIdShape.Count)]
    public void A_self_url_with_no_query_is_not_evidence(RepeatedIdShape shape)
    {
        AssertNotEvidence(Bundle(shape, "http://127.0.0.1:1/Patient"), shape);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary, "Patient#diagnostic?_id=count-female-1")]
    [InlineData(RepeatedIdShape.Count, "not a URL?_id=count-female-1")]
    public void Text_that_is_not_a_self_query_cannot_supply_an_id_occurrence(RepeatedIdShape shape, string linkText)
    {
        AssertNotEvidence(Bundle(shape, linkText), shape);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary)]
    [InlineData(RepeatedIdShape.Count)]
    public void No_self_link_is_not_evidence(RepeatedIdShape shape)
    {
        AssertNotEvidence(Bundle(shape, selfUrl: null), shape);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary)]
    [InlineData(RepeatedIdShape.Count)]
    public void Two_self_links_are_ambiguous_and_not_evidence(RepeatedIdShape shape)
    {
        string body = Bundle(shape, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1"), extraSelfUrl: SelfUrl("_id=count-female-1"));

        AssertNotEvidence(body, shape);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary)]
    [InlineData(RepeatedIdShape.Count)]
    public void A_self_link_without_a_url_is_not_evidence(RepeatedIdShape shape)
    {
        string body = Bundle(shape, selfUrl: null).Replace("\"link\":[]", "\"link\":[{\"relation\":\"self\"}]", StringComparison.Ordinal);

        AssertNotEvidence(body, shape);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary)]
    [InlineData(RepeatedIdShape.Count)]
    public void A_next_link_is_not_evidence(RepeatedIdShape shape)
    {
        string body = Bundle(shape, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1"), nextUrl: UntrustedUrl);

        BackendEvidenceException failure = AssertNotEvidence(body, shape);

        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RepeatedIdShape.Primary, "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":1,\"link\":[{\"relation\":\"self\",\"url\":\"a?_id=count-female-1\"}],\"link\":[{\"relation\":\"self\",\"url\":\"a?_id=count-female-1&_id=count-female-1\"}]}")]
    [InlineData(RepeatedIdShape.Count, "{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":1,\"link\":[{\"relation\":\"self\",\"url\":\"a?_id=count-female-1\"}],\"link\":[{\"relation\":\"self\",\"url\":\"a?_id=count-female-1&_id=count-female-1\"}]}")]
    [InlineData(RepeatedIdShape.Primary, "not json")]
    [InlineData(RepeatedIdShape.Count, "{\"resourceType\":\"OperationOutcome\"}")]
    public void A_duplicate_member_or_a_non_searchset_is_not_evidence(RepeatedIdShape shape, string body)
    {
        BackendEvidenceException failure = AssertNotEvidence(body, shape);

        Assert.DoesNotContain(body, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_with_an_entry_is_not_evidence()
    {
        string body = Bundle(RepeatedIdShape.Primary, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1"));

        AssertNotEvidence(body, RepeatedIdShape.Count);
    }

    [Fact]
    public void A_primary_whose_total_differs_from_its_entries_is_not_evidence()
    {
        string body = Bundle(RepeatedIdShape.Primary, SelfUrl("_id=count-female-1&%5Fid=%63ount-female-1"), total: 2);

        AssertNotEvidence(body, RepeatedIdShape.Primary);
    }

    [Fact]
    public void Reports_hold_only_the_label_and_counts_and_never_response_text()
    {
        string body = Bundle(
            RepeatedIdShape.Primary,
            $"{UntrustedUrl}&_id=count-female-1&%5Fid=%63ount-female-1",
            [UnreservedId],
            total: 1);

        RepeatedIdEvidence evidence = RepeatedIdEvidence.Parse(body, RepeatedIdShape.Primary, SeededId);

        string report = evidence.Describe("GET primary");
        Assert.DoesNotContain(UnreservedId, report, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-this-url", report, StringComparison.Ordinal);
        Assert.DoesNotContain("_getpages", report, StringComparison.Ordinal);
        Assert.False(evidence.ResultAsExpected);
    }

    private static readonly Uri BaseAddress = new("http://127.0.0.1:45123/");

    [Fact]
    public void Only_get_and_post_have_a_repeated_id_request_shape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackendHttpClient.BuildRepeatedIdRequest(HttpMethod.Put, RepeatedIdShape.Primary, BaseAddress));
    }

    [Theory]
    [InlineData("GET", RepeatedIdShape.Primary, "GET /Patient?_id=count-female-1&%5Fid=%63ount-female-1&_count=100&_total=accurate HTTP/1.1", "")]
    [InlineData("GET", RepeatedIdShape.Count, "GET /Patient?_id=count-female-1&%5Fid=%63ount-female-1&_count=0 HTTP/1.1", "")]
    [InlineData("POST", RepeatedIdShape.Primary, "POST /Patient/_search HTTP/1.1", "_id=count-female-1&%5Fid=%63ount-female-1&_count=100&_total=accurate")]
    [InlineData("POST", RepeatedIdShape.Count, "POST /Patient/_search HTTP/1.1", "_id=count-female-1&%5Fid=%63ount-female-1&_count=0")]
    public async Task The_real_client_puts_the_literal_escapes_on_the_wire_and_returns_the_parsed_evidence(
        string method, RepeatedIdShape shape, string expectedRequestLine, string expectedBody)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync(Bundle(shape, SelfUrl("_id=count-female-1&_count=100")), cancellationToken);
        using var client = new BackendHttpClient(server.BaseAddress);

        RepeatedIdEvidence evidence = await client.SearchRepeatedIdAsync(new HttpMethod(method), shape, cancellationToken);

        CapturedRequest wire = await served;
        Assert.Equal(expectedRequestLine, wire.RequestLine);
        Assert.Equal(expectedBody, wire.Body);
        Assert.Contains("Prefer: handling=strict", wire.Headers, StringComparison.Ordinal);
        if (method == "POST")
        {
            Assert.Contains("Content-Type: application/x-www-form-urlencoded", wire.Headers, StringComparison.Ordinal);
        }

        Assert.True(evidence.ResultAsExpected);
        Assert.Equal(RepeatedIdEcho.Collapsed, evidence.Echo);
    }

    [Fact]
    public async Task A_rejected_repeated_id_search_names_the_method_and_shape_and_is_not_a_result()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var server = new LoopbackFhirServer();
        Task<CapturedRequest> served = server.ServeOnceAsync("{\"resourceType\":\"OperationOutcome\"}", cancellationToken, status: 400);
        using var client = new BackendHttpClient(server.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            client.SearchRepeatedIdAsync(HttpMethod.Post, RepeatedIdShape.Count, cancellationToken));
        await served;

        Assert.Equal(BackendFailureKind.UnsupportedCapability, failure.Kind);
        Assert.Contains("POST count", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 400", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationOutcome", failure.Message, StringComparison.Ordinal);
    }

    private static BackendEvidenceException AssertNotEvidence(string body, RepeatedIdShape shape)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => RepeatedIdEvidence.Parse(body, shape, SeededId));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        return failure;
    }

    private static string SelfUrl(string query) => $"http://127.0.0.1:1/Patient?{query}";

    private static string Bundle(
        RepeatedIdShape shape,
        string? selfUrl,
        string[]? ids = null,
        int? total = null,
        string? extraSelfUrl = null,
        string? nextUrl = null)
    {
        var links = new JsonArray();
        foreach (string url in new[] { selfUrl, extraSelfUrl }.OfType<string>())
        {
            links.Add(new JsonObject { ["relation"] = "self", ["url"] = url });
        }

        if (nextUrl is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = nextUrl });
        }

        var bundle = new JsonObject { ["resourceType"] = "Bundle", ["type"] = "searchset", ["link"] = links };
        if (shape == RepeatedIdShape.Primary)
        {
            string[] entryIds = ids ?? [SeededId];
            var entries = new JsonArray();
            foreach (string id in entryIds)
            {
                entries.Add(new JsonObject
                {
                    ["resource"] = new JsonObject { ["resourceType"] = "Patient", ["id"] = id },
                    ["search"] = new JsonObject { ["mode"] = "match" },
                });
            }

            bundle["entry"] = entries;
            bundle["total"] = total ?? entryIds.Length;
        }
        else
        {
            bundle["total"] = total ?? 1;
        }

        return bundle.ToJsonString();
    }
}

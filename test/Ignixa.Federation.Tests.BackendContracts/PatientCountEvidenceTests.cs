using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the ordinary-count evidence path. They run without Docker and show
/// that a count response is accepted only as a complete, entry-free searchset, that an ignored filter
/// is reported, and that nothing the server sent is echoed.
/// </summary>
public sealed class PatientCountEvidenceTests
{
    private const string UntrustedUrl = "https://do-not-log-this-url.invalid/Patient?_getpages=secret";
    private const string UnreservedId = "do-not-log-this-id";

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_searchset_with_a_total_and_no_entries_is_the_count(int total)
    {
        CountEvidence evidence = CountEvidence.Parse(Count(total));

        Assert.Equal(total, evidence.Total);
    }

    [Fact]
    public void A_zero_total_is_a_count_and_not_a_missing_total()
    {
        CountEvidence evidence = CountEvidence.Parse("""{"resourceType":"Bundle","type":"searchset","total":0}""");

        Assert.Equal(0, evidence.Total);
    }

    [Fact]
    public void An_empty_entry_array_and_a_self_link_are_still_a_count()
    {
        string body = """
            {"resourceType":"Bundle","type":"searchset","total":2,"entry":[],
             "link":[{"relation":"self","url":"http://127.0.0.1:1/Patient?_count=0"}]}
            """;

        Assert.Equal(2, CountEvidence.Parse(body).Total);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"total\":\"3\"")]
    [InlineData(",\"total\":null")]
    [InlineData(",\"total\":true")]
    [InlineData(",\"total\":-1")]
    [InlineData(",\"total\":1.5")]
    [InlineData(",\"total\":3.0")]
    [InlineData(",\"total\":3000000000")]
    [InlineData(",\"total\":[3]")]
    [InlineData(",\"total\":{\"value\":3}")]
    public void A_missing_or_invalid_total_is_not_evidence(string totalMember)
    {
        string body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"" + totalMember + "}";

        AssertNotEvidence(body);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"resourceType\":\"OperationOutcome\",\"total\":3}")]
    [InlineData("{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"total\":3}")]
    [InlineData("{\"resourceType\":\"Bundle\",\"total\":3}")]
    [InlineData("{\"resourceType\":\"Bundle\",\"type\":7,\"total\":3}")]
    public void A_response_that_is_not_a_searchset_bundle_is_not_evidence(string body)
    {
        BackendEvidenceException failure = AssertNotEvidence(body);

        Assert.DoesNotContain(body, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_means_the_count_request_was_not_honored_and_only_the_number_is_reported()
    {
        string body = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = 3,
            ["entry"] = new JsonArray(new JsonObject
            {
                ["fullUrl"] = UntrustedUrl,
                ["resource"] = new JsonObject { ["resourceType"] = "Patient", ["id"] = UnreservedId },
            }),
        }.ToJsonString();

        BackendEvidenceException failure = AssertNotEvidence(body);

        Assert.Contains("1 entries", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedId, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"entry\":{}")]
    [InlineData("\"entry\":null")]
    [InlineData("\"entry\":\"none\"")]
    public void A_malformed_entry_member_is_not_evidence(string entryMember)
    {
        AssertNotEvidence("{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":3," + entryMember + "}");
    }

    [Fact]
    public void A_next_link_means_the_count_request_was_paged_and_the_url_is_not_reported()
    {
        string body = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["total"] = 3,
            ["link"] = new JsonArray(new JsonObject { ["relation"] = "next", ["url"] = UntrustedUrl }),
        }.ToJsonString();

        BackendEvidenceException failure = AssertNotEvidence(body);

        Assert.DoesNotContain(UntrustedUrl, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"link\":{\"relation\":\"next\"}")]
    [InlineData("\"link\":\"next\"")]
    [InlineData("\"link\":null")]
    [InlineData("\"link\":[null]")]
    [InlineData("\"link\":[{}]")]
    [InlineData("\"link\":[{\"relation\":5}]")]
    [InlineData("\"link\":[{\"relation\":\"\"}]")]
    public void A_malformed_link_member_is_not_evidence(string linkMember)
    {
        AssertNotEvidence("{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":3," + linkMember + "}");
    }

    [Theory]
    [InlineData("\"entry\":[{}],\"entry\":[]")]
    [InlineData("\"link\":[{\"relation\":\"next\",\"url\":\"https://redacted.invalid\"}],\"link\":[]")]
    [InlineData("\"link\":[{\"relation\":\"next\",\"relation\":\"self\",\"url\":\"https://redacted.invalid\"}]")]
    [InlineData("\"link\":[{\"relation\":\"self\"}]")]
    public void Ambiguous_or_incomplete_count_fields_are_not_evidence(string members)
    {
        AssertNotEvidence("{\"resourceType\":\"Bundle\",\"type\":\"searchset\",\"total\":3," + members + "}");
    }

    [Fact]
    public void A_total_that_equals_the_literal_matches_and_is_reported_as_matched()
    {
        CountEvidence evidence = CountEvidence.Parse(Count(2));

        Assert.True(evidence.Matches(2));
        Assert.Contains("matched", evidence.Describe("GET gender=female", 2), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gender=female", 2)]
    [InlineData("gender=unknown", 0)]
    public void Break_ignored_filter_returns_the_unfiltered_total_and_is_reported(string filter, int expected)
    {
        // A backend that ignores the filter answers every query with the full seed count of 3.
        CountEvidence ignoredFilter = CountEvidence.Parse(Count(3));

        Assert.False(ignoredFilter.Matches(expected));
        string report = ignoredFilter.Describe($"GET {filter}", expected);
        Assert.StartsWith("[contract]", report, StringComparison.Ordinal);
        Assert.Contains($"expected total: {expected}; actual total: 3", report, StringComparison.Ordinal);
        Assert.Contains($"GET {filter}", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Break_filter_applied_to_the_empty_query_is_reported()
    {
        CountEvidence overFiltered = CountEvidence.Parse(Count(2));

        Assert.False(overFiltered.Matches(3));
    }

    [Theory]
    [InlineData("GET", null, "Patient?_count=0")]
    [InlineData("GET", "female", "Patient?_count=0&gender=female")]
    [InlineData("GET", "unknown", "Patient?_count=0&gender=unknown")]
    public async Task The_get_count_request_is_ordinary_count_zero_with_strict_handling(string method, string? gender, string expectedTarget)
    {
        using HttpRequestMessage request = BackendHttpClient.BuildCountRequest(new HttpMethod(method), gender);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(expectedTarget, request.RequestUri!.OriginalString);
        Assert.Null(request.Content);
        await AssertStrictHandlingOnlyAsync(request);
    }

    [Theory]
    [InlineData(null, "_count=0")]
    [InlineData("female", "_count=0&gender=female")]
    [InlineData("unknown", "_count=0&gender=unknown")]
    public async Task The_post_count_request_carries_count_zero_in_the_form_body_with_strict_handling(string? gender, string expectedBody)
    {
        using HttpRequestMessage request = BackendHttpClient.BuildCountRequest(HttpMethod.Post, gender);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Patient/_search", request.RequestUri!.OriginalString);
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(expectedBody, await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await AssertStrictHandlingOnlyAsync(request);
    }

    [Fact]
    public void Only_get_and_post_have_a_count_request_shape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BackendHttpClient.BuildCountRequest(HttpMethod.Put, null));
    }

    [Fact]
    public void The_count_seed_is_synthetic_patients_with_two_female_and_one_male()
    {
        IReadOnlyList<SeedResource> resources = CountSeedData.Seed.Resources("http://127.0.0.1:45123/");

        Assert.All(resources, r => Assert.Equal("Patient", r.ResourceType));
        string[] genders = [.. resources.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => JsonNode.Parse(r.Json)!["gender"]!.GetValue<string>())];
        Assert.Equal(["female", "female", "male"], genders);
        Assert.Equal("Patient", CountSeedData.Seed.ResourceType);
        Assert.Equal(
            [.. resources.Select(r => r.Id).Order(StringComparer.Ordinal)],
            [.. CountSeedData.Seed.Ids.Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task Readiness_for_the_count_seed_uses_a_positive_id_page_of_the_seeded_patients()
    {
        string? query = null;
        string[] ids = [.. CountSeedData.Seed.Ids];

        await MicrosoftFhirSqlBackend.WaitForSeedSearchableAsync(
            (q, _) =>
            {
                query = q;
                return Task.FromResult(SearchsetEvidence.Parse(PatientPage(ids), "Patient"));
            },
            CountSeedData.Seed.ResourceType,
            CountSeedData.Seed.Ids,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal($"_id={string.Join(',', CountSeedData.Seed.Ids)}", query);
    }

    [Fact]
    public async Task Readiness_timeout_for_the_count_seed_names_the_patients_and_hides_other_ids()
    {
        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            MicrosoftFhirSqlBackend.WaitForSeedSearchableAsync(
                (_, _) => Task.FromResult(SearchsetEvidence.Parse(PatientPage([UnreservedId]), "Patient")),
                CountSeedData.Seed.ResourceType,
                CountSeedData.Seed.Ids,
                TimeSpan.FromMilliseconds(20),
                TestContext.Current.CancellationToken));

        Assert.Contains("0 of 3 seeded Patients (1 total matches)", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedId, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_searchset_of_patients_is_only_complete_for_the_patient_type()
    {
        string page = PatientPage(["count-a"]);

        Assert.Equal(["count-a"], SearchsetEvidence.Parse(page, "Patient").MatchIds);
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(page));
        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    private static async Task AssertStrictHandlingOnlyAsync(HttpRequestMessage request)
    {
        Assert.Equal(["handling=strict"], request.Headers.GetValues("Prefer"));
        Assert.DoesNotContain("_total", request.RequestUri!.OriginalString, StringComparison.Ordinal);
        if (request.Content is not null)
        {
            Assert.DoesNotContain("_total", await request.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
    }

    private static BackendEvidenceException AssertNotEvidence(string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => CountEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        return failure;
    }

    private static string Count(int total) =>
        new JsonObject { ["resourceType"] = "Bundle", ["type"] = "searchset", ["total"] = total }.ToJsonString();

    private static string PatientPage(string[] ids)
    {
        var entries = new JsonArray();
        foreach (string id in ids)
        {
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
            ["total"] = ids.Length,
            ["entry"] = entries,
        }.ToJsonString();
    }
}

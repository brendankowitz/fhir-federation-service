using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Docker.DotNet;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the evidence path. They run without Docker and show that the
/// contract assertions fail for the documented breaks (type/id-only matching, literal URL equality,
/// and a host-ignoring foreign query), so a green live run means something.
/// The canned searchsets stand in for what a broken backend would return.
/// </summary>
public sealed class BackendEvidenceTests
{
    [Theory]
    [InlineData("5.2.84")]
    [InlineData("5.2.84.0")]
    public void Identity_uses_the_software_fields_from_the_capability_statement(string version)
    {
        string metadata = $$$"""
            {"resourceType":"CapabilityStatement","name":"Not the software name","version":"0.0.0",
             "software":{"name":"Sample FHIR Backend","version":"{{{version}}}"}}
            """;

        Assert.Equal($"Sample FHIR Backend {version}", BackendHttpClient.ReadSoftwareIdentity(metadata));
    }

    [Fact]
    public void Identity_rejects_metadata_that_could_inject_a_log_line()
    {
        const string metadata = """
            {"resourceType":"CapabilityStatement","software":{"name":"Example\n::warning::Do not print",
             "version":"5.2.84"}}
            """;

        var failure = Assert.Throws<BackendEvidenceException>(() => BackendHttpClient.ReadSoftwareIdentity(metadata));

        Assert.Equal(BackendFailureKind.Startup, failure.Kind);
        Assert.DoesNotContain("Do not print", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Metadata_probe_reports_the_http_status_but_not_the_error_body()
    {
        const string UntrustedBody = "untrusted diagnostic body";
        using var server = new LoopbackFhirServer();
        using var client = new BackendHttpClient(server.BaseAddress);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Task<CapturedRequest> served = server.ServeOnceAsync(UntrustedBody, cancellationToken, (int)HttpStatusCode.ServiceUnavailable);

        var failure = await Assert.ThrowsAsync<BackendEvidenceException>(() => client.GetSoftwareIdentityAsync(cancellationToken));

        Assert.Equal(BackendFailureKind.Startup, failure.Kind);
        Assert.Contains("HTTP 503", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedBody, failure.Message, StringComparison.Ordinal);
        Assert.StartsWith("GET /metadata ", (await served).RequestLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_log_tail_keeps_the_fatal_line_after_an_overlong_line()
    {
        const string Password = "synthetic-sql-password";
        string input = $"earlier message\n{new string('x', 30 * 1024)}{Password}\nfatal schema error: {Password}\r\n";
        using var logs = new MultiplexedStream(new MemoryStream(Encoding.UTF8.GetBytes(input)), false);

        string diagnostic = await MicrosoftFhirSqlBackend.ReadContainerLogTailAsync(
            logs, "SQL", Password, TestContext.Current.CancellationToken);

        Assert.Equal("[SQL startup log tail, truncated]\n[SQL] fatal schema error: [redacted]", diagnostic);
    }

    [Fact]
    public async Task Startup_log_tail_keeps_a_complete_line_starting_exactly_at_the_cutoff()
    {
        const string LastLine = "fatal schema error";
        string firstRetainedLine = new('x', 12 * 1024 - LastLine.Length - 2);
        string input = $"discarded\n{firstRetainedLine}\n{LastLine}\n";
        using var logs = new MultiplexedStream(new MemoryStream(Encoding.UTF8.GetBytes(input)), false);

        string diagnostic = await MicrosoftFhirSqlBackend.ReadContainerLogTailAsync(
            logs, "SQL", "synthetic-sql-password", TestContext.Current.CancellationToken);

        Assert.Equal($"[SQL startup log tail, truncated]\n[SQL] {firstRetainedLine}\n[SQL] {LastLine}", diagnostic);
    }

    [Fact]
    public async Task Startup_log_tail_omits_an_unterminated_line_that_may_contain_a_partial_password()
    {
        const string Input = "complete message\npassword=synthetic-sql-pass";
        using var logs = new MultiplexedStream(new MemoryStream(Encoding.UTF8.GetBytes(Input)), false);

        string diagnostic = await MicrosoftFhirSqlBackend.ReadContainerLogTailAsync(
            logs, "SQL", "synthetic-sql-password", TestContext.Current.CancellationToken);

        Assert.Equal("[SQL startup log tail, truncated]\n[SQL] complete message", diagnostic);
    }

    private const string LocalQueryDescription = "subject=<own base>/Patient/dup";

    private static readonly string[] LocalQueryExpected = ["obs-own-absolute-dup", "obs-relative-dup"];

    [Fact]
    public void Break_type_and_id_only_matching_admits_the_foreign_reference_and_is_reported()
    {
        SearchsetEvidence typeIdOnlyBackend = SearchsetEvidence.Parse(
            Searchset("obs-relative-dup", "obs-own-absolute-dup", "obs-foreign-absolute-dup", "obs-foreign-b-absolute-dup"));

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Contract, LocalQueryDescription, LocalQueryExpected, typeIdOnlyBackend);

        Assert.False(comparison.IsEqual);
        string report = comparison.Describe();
        Assert.StartsWith("[contract]", report, StringComparison.Ordinal);
        Assert.Contains("unexpected: [obs-foreign-absolute-dup, obs-foreign-b-absolute-dup]", report, StringComparison.Ordinal);
        Assert.Contains("missing: []", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Break_host_ignoring_foreign_query_also_returns_the_other_foreign_host_and_is_reported()
    {
        // A backend that ignores the host answers the foreign A query with both foreign references.
        // The shared foreign A comparison is the one the live case uses, so this fails if the live
        // expectation ever accepts foreign B.
        SearchsetEvidence hostIgnoringBackend = SearchsetEvidence.Parse(
            Searchset(SeedData.ForeignAbsoluteObservationId, SeedData.OtherForeignAbsoluteObservationId));

        MatchComparison comparison = ReferenceSearchContractTests.CompareForeignAQuery(hostIgnoringBackend);

        Assert.False(comparison.IsEqual);
        string report = comparison.Describe();
        Assert.Contains("unexpected: [obs-foreign-b-absolute-dup]", report, StringComparison.Ordinal);
        Assert.Contains("missing: []", report, StringComparison.Ordinal);

        SearchsetEvidence exactBackend = SearchsetEvidence.Parse(Searchset(SeedData.ForeignAbsoluteObservationId));
        Assert.True(ReferenceSearchContractTests.CompareForeignAQuery(exactBackend).IsEqual);
    }

    [Fact]
    public void Outgoing_observations_seed_the_exact_reference_spelling_for_each_id()
    {
        var subjects = SeedData.Resources("http://127.0.0.1:45123/custom/r4/")
            .Select(r => JsonNode.Parse(r.Json)!)
            .Where(json => json["resourceType"]!.GetValue<string>() == "Observation")
            .ToDictionary(json => json["id"]!.GetValue<string>(), json => json["subject"]!["reference"]!.GetValue<string>());

        Assert.Equal(5, subjects.Count);
        Assert.Equal("Patient/dup", subjects["obs-relative-dup"]);
        Assert.Equal("http://127.0.0.1:45123/custom/r4/Patient/dup", subjects["obs-own-absolute-dup"]);
        Assert.Equal("http://foreign-shard.invalid:45123/custom/r4/Patient/dup", subjects["obs-foreign-absolute-dup"]);
        Assert.Equal("http://foreign-shard-b.invalid:45123/custom/r4/Patient/dup", subjects["obs-foreign-b-absolute-dup"]);
        Assert.Equal("Patient/other", subjects["obs-control-other"]);
    }

    [Theory]
    [InlineData("server-error", "incomplete search response: the search returned unexpected HTTP 500")]
    [InlineData("missing-total", "incomplete search response: the searchset has no total")]
    [InlineData("missing-seeds", "complete search page contained 1 of 5 seeded Observations (2 total matches)")]
    public async Task Seed_readiness_timeout_reports_the_last_safe_observation(string response, string expectedDetail)
    {
        const string UnreservedId = "do-not-log-this-id";
        const string UntrustedLink = "https://do-not-log-this-url.invalid/";

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            MicrosoftFhirSqlBackend.WaitForSeedSearchableAsync(
                (_, _) => response switch
                {
                    "server-error" => Task.FromException<SearchsetEvidence>(new BackendEvidenceException(
                        BackendFailureKind.IncompleteSearch, "the search returned unexpected HTTP 500")),
                    "missing-total" => Task.FromResult(SearchsetEvidence.Parse(
                        Build(["obs-relative-dup"], total: null, selfLink: UntrustedLink))),
                    _ => Task.FromResult(SearchsetEvidence.Parse(
                        Build(["obs-relative-dup", UnreservedId], selfLink: UntrustedLink))),
                },
                "Observation",
                SeedData.AllObservationIds,
                TimeSpan.FromMilliseconds(20),
                TestContext.Current.CancellationToken));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.Contains(expectedDetail, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UnreservedId, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UntrustedLink, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Break_literal_url_equality_excludes_the_relative_reference_and_is_reported()
    {
        SearchsetEvidence literalEqualityBackend = SearchsetEvidence.Parse(Searchset("obs-own-absolute-dup"));

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Contract, LocalQueryDescription, LocalQueryExpected, literalEqualityBackend);

        Assert.False(comparison.IsEqual);
        string report = comparison.Describe();
        Assert.Contains("missing: [obs-relative-dup]", report, StringComparison.Ordinal);
        Assert.Contains("unexpected: []", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_ids_in_any_order_are_equal()
    {
        SearchsetEvidence actual = SearchsetEvidence.Parse(Searchset("obs-relative-dup", "obs-own-absolute-dup"));

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Contract, LocalQueryDescription, LocalQueryExpected, actual);

        Assert.True(comparison.IsEqual, comparison.Describe());
    }

    [Fact]
    public void A_repeated_match_is_a_multiset_difference()
    {
        SearchsetEvidence actual = SearchsetEvidence.Parse(
            Searchset("obs-relative-dup", "obs-relative-dup", "obs-own-absolute-dup"));

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Contract, LocalQueryDescription, LocalQueryExpected, actual);

        Assert.False(comparison.IsEqual);
        Assert.Contains("unexpected: [obs-relative-dup]", comparison.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Characterization_reports_use_their_own_label()
    {
        SearchsetEvidence actual = SearchsetEvidence.Parse(Searchset("obs-relative-dup"));

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Characterization, "subject=Patient/dup", ["obs-relative-dup", "obs-own-absolute-dup"], actual);

        Assert.StartsWith("[characterization]", comparison.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ids_outside_the_reserved_set_are_not_echoed_and_long_lists_are_bounded()
    {
        string[] stray = Enumerable.Range(0, 40).Select(i => $"stray-id-{i:D4}-with-a-long-tail-{new string('x', 60)}").ToArray();
        SearchsetEvidence actual = SearchsetEvidence.Parse(Build([.. stray, "obs-relative-dup"]));

        string report = MatchComparison.Compare(
            EvidenceLabel.Contract, LocalQueryDescription, LocalQueryExpected, actual).Describe();

        Assert.DoesNotContain("stray-id-", report, StringComparison.Ordinal);
        Assert.Contains("unreserved", report, StringComparison.Ordinal);
        Assert.InRange(report.Length, 1, 1_500);
    }

    [Fact]
    public void Parse_returns_match_ids_sorted_with_ordinal_order()
    {
        SearchsetEvidence evidence = SearchsetEvidence.Parse(Searchset("obs-relative-dup", "obs-control-other"));

        Assert.Equal(["obs-control-other", "obs-relative-dup"], evidence.MatchIds);
    }

    [Fact]
    public void A_next_link_means_the_evidence_is_incomplete()
    {
        string pageOne = Build(["obs-relative-dup"], nextLink: "http://127.0.0.1:1/Observation?_getpages=x");

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(pageOne));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void A_duplicate_link_cannot_hide_a_next_page()
    {
        const string page = """
            {"resourceType":"Bundle","type":"searchset","total":1,
             "link":[{"relation":"next","url":"Observation?_getpages=x"}],"link":[],
             "entry":[{"resource":{"resourceType":"Observation","id":"obs-relative-dup"},
                       "search":{"mode":"match"}}]}
            """;

        var failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(page));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void A_duplicate_search_mode_cannot_turn_an_include_into_a_match()
    {
        const string page = """
            {"resourceType":"Bundle","type":"searchset","total":1,
             "entry":[{"resource":{"resourceType":"Observation","id":"obs-relative-dup"},
                       "search":{"mode":"include","mode":"match"}}]}
            """;

        var failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(page));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void A_total_that_differs_from_the_match_count_means_the_evidence_is_incomplete()
    {
        string truncated = Build(["obs-relative-dup"], total: 2);

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(truncated));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void A_missing_total_means_the_evidence_is_incomplete()
    {
        string noTotal = Build(["obs-relative-dup"], total: null);

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(noTotal));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void An_entry_that_is_not_a_match_means_the_evidence_is_incomplete()
    {
        string withOutcome = Build(["obs-relative-dup"], extraEntryMode: "outcome");

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(withOutcome));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"resourceType\":\"OperationOutcome\"}")]
    [InlineData("{\"resourceType\":\"Bundle\",\"type\":\"collection\",\"total\":0}")]
    public void A_response_that_is_not_a_searchset_means_the_evidence_is_incomplete(string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain(body, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"total\":0,\"link\":{\"relation\":\"next\"}")]
    [InlineData("\"total\":0,\"link\":[{}]")]
    [InlineData("\"total\":0,\"entry\":[null]")]
    [InlineData("\"total\":\"0\"")]
    public void Malformed_members_cannot_establish_completeness(string members)
    {
        string body = "{\"resourceType\":\"Bundle\",\"type\":\"searchset\"," + members + "}";

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => SearchsetEvidence.Parse(body));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    /// <summary>Builds a FHIR R4 searchset JSON string. Shape follows the FHIR R4 Bundle definition.</summary>
    private static string Build(
        string[] ids,
        string? nextLink = null,
        int? total = -1,
        string? selfLink = "http://127.0.0.1:1/Observation",
        string? extraEntryMode = null)
    {
        var links = new JsonArray();
        if (selfLink is not null)
        {
            links.Add(new JsonObject { ["relation"] = "self", ["url"] = selfLink });
        }

        if (nextLink is not null)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = nextLink });
        }

        var entries = new JsonArray();
        foreach (string id in ids)
        {
            entries.Add(Entry("Observation", id, "match"));
        }

        if (extraEntryMode is not null)
        {
            entries.Add(Entry("OperationOutcome", id: null, extraEntryMode));
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["link"] = links,
            ["entry"] = entries,
        };

        // -1 means "the number of match ids"; null means "omit total".
        int? effectiveTotal = total == -1 ? ids.Length : total;
        if (effectiveTotal is not null)
        {
            bundle["total"] = effectiveTotal;
        }

        return bundle.ToJsonString();
    }

    private static JsonObject Entry(string resourceType, string? id, string mode)
    {
        var resource = new JsonObject { ["resourceType"] = resourceType };
        if (id is not null)
        {
            resource["id"] = id;
        }

        return new JsonObject
        {
            ["resource"] = resource,
            ["search"] = new JsonObject { ["mode"] = mode },
        };
    }

    private static string Searchset(params string[] ids) => Build(ids);
}
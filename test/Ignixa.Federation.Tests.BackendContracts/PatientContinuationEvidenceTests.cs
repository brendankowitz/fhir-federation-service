using System.Text.Json.Nodes;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Backend-independent checks of the directional continuation witness. They run without Docker. The real
/// <see cref="BackendHttpClient"/> and the real <see cref="ContinuationWitness"/> talk over loopback to a
/// scripted backend that accepts only the exact wire shape the witness must send, so a wrong method, target
/// or query fails instead of being tolerated. Nothing the server sends is echoed in a report.
/// </summary>
public sealed class PatientContinuationEvidenceTests
{
    private const string Secret = "do-not-log-this-token";
    private const string OddSuffix = "&odd=a%2Fb+c%7e%7E";
    private static readonly Uri Base = new("http://127.0.0.1:5000/");
    private static readonly string[] ReservedIds = [.. CountSeedData.Seed.Ids];
    private static readonly string[] OracleOrder = ["count-male-1", "count-female-1", "count-female-2"];

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    [InlineData("POST", true)]
    public async Task Each_verb_and_direction_replays_every_native_link_exactly_twice_and_ends_with_an_absent_next(string method, bool descending)
    {
        await using var backend = new ScriptedPatientBackend(new HttpMethod(method)) { RewriteNext = u => u + OddSuffix };

        (ContinuationReport report, IReadOnlyList<CapturedRequest> wire) = await WalkAsync(backend, descending);

        Assert.True(report.Matched, report.Describe("walk"));
        string sort = descending ? "-_lastUpdated" : "_lastUpdated";
        string[] suffixes = [$"_sort={sort}&_count=1", $"_sort={sort}&_count=1&ct=P1{OddSuffix}", $"_sort={sort}&_count=1&ct=P1{OddSuffix}", $"_sort={sort}&_count=1&ct=P2{OddSuffix}", $"_sort={sort}&_count=1&ct=P2{OddSuffix}"];
        Assert.Equal(3 + suffixes.Length, wire.Count);
        Assert.Equal(ReservedIds.Select(id => $"GET /Patient/{id} HTTP/1.1"), wire.Take(3).Select(r => r.RequestLine));
        foreach ((CapturedRequest request, string suffix) in wire.Skip(3).Zip(suffixes))
        {
            Assert.Contains("Prefer: handling=strict", request.Headers, StringComparison.Ordinal);
            if (method == "GET")
            {
                Assert.Equal($"GET /Patient?{suffix} HTTP/1.1", request.RequestLine);
                Assert.Equal(string.Empty, request.Body);
            }
            else
            {
                Assert.Equal("POST /Patient/_search HTTP/1.1", request.RequestLine);
                Assert.Equal(suffix, request.Body);
                Assert.Contains("Content-Type: application/x-www-form-urlencoded", request.Headers, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task An_empty_terminal_page_after_the_last_row_is_redeemed_twice_and_accepted(string method)
    {
        await using var backend = new ScriptedPatientBackend(new HttpMethod(method)) { EmptyTerminal = true };

        (ContinuationReport report, IReadOnlyList<CapturedRequest> wire) = await WalkAsync(backend, descending: false);

        Assert.True(report.Matched, report.Describe("walk"));
        Assert.Equal(3 + 1 + 2 + 2 + 2, wire.Count);
        Assert.Equal(2, wire.Count(r => (r.RequestLine + r.Body).Contains("ct=P3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Distinct_successors_are_each_redeemed_twice_within_the_fifteen_request_bound()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get) { UniqueTokens = true, EmptyTerminal = true };

        (ContinuationReport report, IReadOnlyList<CapturedRequest> wire) = await WalkAsync(backend, descending: true);

        Assert.True(report.Matched, report.Describe("walk"));
        Assert.Equal(3 + 1 + 2 * (1 + 2 + 4), wire.Count);
        string[] targets = [.. wire.Skip(3).Skip(1).Select(r => r.RequestLine)];
        Assert.Equal(7, targets.Distinct(StringComparer.Ordinal).Count());
        Assert.All(targets.GroupBy(t => t, StringComparer.Ordinal), g => Assert.Equal(2, g.Count()));
    }

    [Theory]
    [InlineData("version", "row 2 differs from the direct read in version or instant")]
    [InlineData("instant", "row 2 differs from the direct read in version or instant")]
    [InlineData("other", "row 2 is not the expected Patient")]
    [InlineData("backward", "row 2 repeats or moves back to an earlier row")]
    [InlineData("missing", "row 2 is missing")]
    public async Task A_physical_mismatch_on_the_second_redemption_fails_even_when_the_first_matched(string mutation, string expected)
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get)
        {
            Override = visit => visit.Position == 1 && visit.Redemption == 1
                ? ScriptedPatientBackend.Page(
                    mutation switch
                    {
                        "version" => [visit.Served[1] with { Version = "9" }],
                        "instant" => [visit.Served[1] with { Instant = "2026-01-01T10:00:01.2010000Z" }],
                        "other" => [new FakeRow("some-other-patient", "1", visit.Served[1].Instant)],
                        "backward" => [visit.Served[0]],
                        _ => [],
                    },
                    visit.DefaultNext)
                : null,
        };

        (ContinuationReport report, _) = await WalkAsync(backend, descending: false);

        Assert.False(report.Matched);
        Assert.Contains(expected, report.Describe("walk"), StringComparison.Ordinal);
        Assert.Contains("redemption 2", report.Describe("walk"), StringComparison.Ordinal);
        Assert.DoesNotContain("some-other-patient", report.Describe("walk"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-start-match", "row 1 is missing")]
    [InlineData("extra-rows", "holds 2 rows")]
    [InlineData("early-end", "ended after row 1")]
    [InlineData("terminal-rows", "an extra row follows the last expected row")]
    [InlineData("terminal-next", "a next link follows the terminal page")]
    public async Task A_missing_or_extra_match_or_a_wrong_exhaustion_is_a_contract_failure(string script, string expected)
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get)
        {
            EmptyTerminal = script is "terminal-rows" or "terminal-next",
            TerminalNext = script == "terminal-next",
            Override = visit => (script, visit.Position) switch
            {
                ("missing-start-match", 0) => ScriptedPatientBackend.Page([], visit.DefaultNext),
                ("extra-rows", 0) => ScriptedPatientBackend.Page([visit.Served[0], visit.Served[1]], visit.DefaultNext),
                ("early-end", 0) => ScriptedPatientBackend.Page([visit.Served[0]], null),
                ("terminal-rows", 3) => ScriptedPatientBackend.Page([visit.Served[2]], null),
                _ => null,
            },
        };

        (ContinuationReport report, _) = await WalkAsync(backend, descending: false);

        Assert.False(report.Matched);
        Assert.Contains(expected, report.Describe("walk"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backend_that_ignores_a_descending_sort_fails_on_the_first_row()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Post) { IgnoreSort = true };

        (ContinuationReport report, _) = await WalkAsync(backend, descending: true);

        Assert.False(report.Matched);
        Assert.Contains("row 1 is not the expected Patient", report.Describe("walk"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_expected_order_comes_from_the_direct_read_instants_and_not_from_seed_or_id_order()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get);

        (ContinuationReport report, _) = await WalkAsync(backend, descending: false);

        string description = report.Describe("walk");
        Assert.True(report.Matched, description);
        int[] positions = [.. OracleOrder.Select(id => description.IndexOf(id, StringComparison.Ordinal))];
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain(-1, positions);
    }

    [Fact]
    public async Task Tied_direct_read_instants_are_not_evidence_and_no_search_is_sent()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get);
        backend.Direct[ScriptedPatientBackend.Middle.Id] = ScriptedPatientBackend.Middle with { Instant = "2026-01-01T11:00:00.1000000+01:00" };

        BackendEvidenceException failure = await AssertFailsAsync(backend, BackendFailureKind.IncompleteSearch);

        Assert.Contains("distinct", failure.Message, StringComparison.Ordinal);
        Assert.All(await backend.StopAsync(), r => Assert.StartsWith("GET /Patient/", r.RequestLine, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_direct_read_of_a_different_patient_is_not_evidence()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get);
        backend.Direct[ScriptedPatientBackend.Middle.Id] = new FakeRow("some-other-patient", "1", ScriptedPatientBackend.Middle.Instant);

        BackendEvidenceException failure = await AssertFailsAsync(backend, BackendFailureKind.IncompleteSearch);

        Assert.DoesNotContain("some-other-patient", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_next_link_off_the_local_origin_fails_before_any_further_request_and_is_not_reported()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get)
        {
            RewriteNext = u => u.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "&secret=" + Secret,
        };

        BackendEvidenceException failure = await AssertFailsAsync(backend, BackendFailureKind.IncompleteSearch);

        Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", failure.Message, StringComparison.Ordinal);
        Assert.Equal(3 + 1, (await backend.StopAsync()).Count);
    }

    [Fact]
    public async Task A_report_never_contains_the_issued_link_or_token()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get)
        {
            RewriteNext = u => u + "&secret=" + Secret,
            Override = visit => visit.Position == 1 && visit.Redemption == 1 ? ScriptedPatientBackend.Page([visit.Served[2]], visit.DefaultNext) : null,
        };

        (ContinuationReport report, IReadOnlyList<CapturedRequest> wire) = await WalkAsync(backend, descending: false);

        Assert.False(report.Matched);
        Assert.DoesNotContain(Secret, report.Describe("walk"), StringComparison.Ordinal);
        Assert.DoesNotContain("ct=", report.Describe("walk"), StringComparison.Ordinal);
        Assert.Contains(wire, r => r.RequestLine.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_wrong_method_is_rejected_by_the_backend_and_reported_as_a_capability_answer()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Post);
        using var client = new BackendHttpClient(backend.BaseAddress);

        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            ContinuationWitness.RunAsync(client, ReservedIds, HttpMethod.Get, PatientSortDirection.Ascending, TestContext.Current.CancellationToken));

        Assert.Equal(BackendFailureKind.UnsupportedCapability, failure.Kind);
        Assert.Contains("GET", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_walk_propagates_the_cancellation()
    {
        await using var backend = new ScriptedPatientBackend(HttpMethod.Get);
        using var client = new BackendHttpClient(backend.BaseAddress);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ContinuationWitness.RunAsync(client, ReservedIds, HttpMethod.Get, PatientSortDirection.Ascending, cancelled.Token));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000/Patient?_sort=_lastUpdated&_count=1&ct=a%2Fb+c%7e%7E")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=1&ct=1")]
    public void A_link_on_the_exact_origin_and_path_keeps_its_query_verbatim(string link)
    {
        ArgumentNullException.ThrowIfNull(link);
        Assert.Equal(link["http://127.0.0.1:5000/Patient?".Length..], NativeContinuation.Create(link, Base).Query);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Patient?ct=1")]
    [InlineData("Patient?ct=1")]
    [InlineData("http://localhost:5000/Patient?ct=1")]
    [InlineData("http://127.0.0.1:5001/Patient?ct=1")]
    [InlineData("http://127.0.0.1:5000:80/Patient?ct=1")]
    [InlineData("https://127.0.0.1:5000/Patient?ct=1")]
    [InlineData("HTTP://127.0.0.1:5000/Patient?ct=1")]
    [InlineData("http://user@127.0.0.1:5000/Patient?ct=1")]
    [InlineData("http://127.0.0.1:5000@evil.invalid/Patient?ct=1")]
    [InlineData("http://127.0.0.1:5000/Observation?ct=1")]
    [InlineData("http://127.0.0.1:5000/Patient/_search?ct=1")]
    [InlineData("http://127.0.0.1:5000/Patient/?ct=1")]
    [InlineData("http://127.0.0.1:5000//Patient?ct=1")]
    [InlineData("http://127.0.0.1:5000/./Patient?ct=1")]
    [InlineData("http://127.0.0.1:5000/%50atient?ct=1")]
    [InlineData("http://127.0.0.1:5000/Patient%3Fct=1")]
    [InlineData("http://127.0.0.1:5000/Patient")]
    [InlineData("http://127.0.0.1:5000/Patient?")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=1#fragment")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=1 2")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=1\\2")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=\u00e9")]
    [InlineData("http://127.0.0.1:5000/Patient?ct=1\r\nHost: evil.invalid")]
    public void A_link_that_is_not_on_the_exact_origin_and_path_is_rejected_without_being_reported(string link)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => NativeContinuation.Create(link, Base));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain("evil.invalid", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ct=", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"resourceType":"Bundle","type":"collection"}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","entry":{}}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","entry":[1]}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","entry":[],"entry":[]}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","link":[{"url":"http://127.0.0.1:5000/Patient?x=1"}]}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","link":[{"relation":"next"}]}""")]
    [InlineData("""{"resourceType":"Bundle","type":"searchset","link":[{"relation":"next","url":"http://127.0.0.1:5000/Patient?a=1"},{"relation":"next","url":"http://127.0.0.1:5000/Patient?a=2"}]}""")]
    public void A_page_that_is_not_a_searchset_with_at_most_one_safe_next_link_is_not_evidence(string body)
    {
        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => ContinuationPage.Parse(body, Base));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
        Assert.DoesNotContain("Patient?a=", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("include", "Patient", "p1", "1", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Observation", "p1", "1", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Patient", "", "1", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Patient", "has space", "1", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Patient", "p1", "", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Patient", "p1", "v 1", "2026-01-01T10:00:00Z")]
    [InlineData("match", "Patient", "p1", "1", "2026-01-01T10:00:00")]
    [InlineData("match", "Patient", "p1", "1", "2026-01-01")]
    [InlineData("match", "Patient", "p1", "1", "2026-01-01T10:00:00,5Z")]
    [InlineData("match", "Patient", "p1", "1", "yesterday")]
    public void An_entry_that_is_not_a_patient_match_with_a_safe_id_version_and_instant_is_not_evidence(
        string mode, string type, string id, string version, string instant)
    {
        string body = ScriptedPatientBackend.Page([new FakeRow(id, version, instant)], null, mode, type, rawInstant: true);

        BackendEvidenceException failure = Assert.Throws<BackendEvidenceException>(() => ContinuationPage.Parse(body, Base));

        Assert.Equal(BackendFailureKind.IncompleteSearch, failure.Kind);
    }

    [Fact]
    public void The_same_instant_in_different_spellings_is_one_instant()
    {
        string[] spellings = ["2026-01-01T10:00:02.3000000Z", "2026-01-01T10:00:02.3+00:00", "2026-01-01T05:00:02.3000000-05:00"];

        PatientRow[] rows = [.. spellings.Select(s => PatientRow.ParseRead(
            ScriptedPatientBackend.Resource(new FakeRow("p1", "1", s), s).ToJsonString(), "p1"))];

        Assert.All(rows, r => Assert.Equal(rows[0], r));
    }

    [Fact]
    public void The_count_seed_spaces_its_writes_so_the_patients_get_distinct_instants()
    {
        Assert.InRange(CountSeedData.Seed.WriteSpacing, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
    }

    private static async Task<(ContinuationReport Report, IReadOnlyList<CapturedRequest> Wire)> WalkAsync(ScriptedPatientBackend backend, bool descending)
    {
        using var client = new BackendHttpClient(backend.BaseAddress);
        ContinuationReport report = await ContinuationWitness.RunAsync(
            client,
            ReservedIds,
            backend.ExpectedMethod,
            descending ? PatientSortDirection.Descending : PatientSortDirection.Ascending,
            TestContext.Current.CancellationToken);
        return (report, await backend.StopAsync());
    }

    private static async Task<BackendEvidenceException> AssertFailsAsync(ScriptedPatientBackend backend, BackendFailureKind kind)
    {
        using var client = new BackendHttpClient(backend.BaseAddress);
        BackendEvidenceException failure = await Assert.ThrowsAsync<BackendEvidenceException>(() =>
            ContinuationWitness.RunAsync(client, ReservedIds, HttpMethod.Get, PatientSortDirection.Ascending, TestContext.Current.CancellationToken));
        Assert.Equal(kind, failure.Kind);
        return failure;
    }
}

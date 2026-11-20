namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Ordinary count behavior of a real Microsoft FHIR Server R4 on SQL, witnessed over FHIR HTTP. The
/// federation sums per-shard totals for a search and needs each shard to answer <c>_count=0</c> with
/// a total that honors the filter, as a plain GET and as a form POST to <c>Patient/_search</c>.
///
/// One seeded backend serves six independently reported cases: two methods against three filters.
/// The expected totals are literals written from the seed (two female and one male synthetic Patient),
/// never read from a response and never computed from the seed code. Requests send no <c>_total</c>,
/// so the total is the backend's answer to <c>_count=0</c> alone, and they send
/// <c>Prefer: handling=strict</c>. A backend that ignores the filter answers 3 for the female and
/// unknown cases, which fails them. A response that is not a count (wrong shape, entries, a next
/// link, no total) is not evidence and fails as <c>[incomplete-search]</c>.
/// </summary>
[Collection(LiveCountBackendDefinition.Name)]
public sealed class PatientCountContractTests(PatientCountBackendFixture backend, ITestOutputHelper output)
{
    private const int CaseTimeoutMilliseconds = 120_000;

    /// <summary>Contract. <c>GET Patient?_count=0</c> with an optional gender filter reports the matching total.</summary>
    [Theory(Timeout = CaseTimeoutMilliseconds)]
    [InlineData(null, 3)]
    [InlineData("female", 2)]
    [InlineData("unknown", 0)]
    public Task Contract_get_count_zero_reports_the_filtered_total(string? gender, int expectedTotal) =>
        AssertCountAsync(HttpMethod.Get, gender, expectedTotal, TestContext.Current.CancellationToken);

    /// <summary>Contract. <c>POST Patient/_search</c> with <c>_count=0</c> in the form body reports the same totals.</summary>
    [Theory(Timeout = CaseTimeoutMilliseconds)]
    [InlineData(null, 3)]
    [InlineData("female", 2)]
    [InlineData("unknown", 0)]
    public Task Contract_post_search_count_zero_reports_the_filtered_total(string? gender, int expectedTotal) =>
        AssertCountAsync(HttpMethod.Post, gender, expectedTotal, TestContext.Current.CancellationToken);

    private async Task AssertCountAsync(HttpMethod method, string? gender, int expectedTotal, CancellationToken cancellationToken)
    {
        output.WriteLine($"Backend /metadata software: {backend.SoftwareIdentity}");
        CountEvidence actual = await backend.CountPatientsAsync(method, gender, cancellationToken);

        string evidence = actual.Describe($"{method.Method} Patient _count=0 {(gender is null ? "no filter" : $"gender={gender}")}", expectedTotal);
        Assert.True(actual.Matches(expectedTotal), evidence);
        output.WriteLine(evidence);
    }
}

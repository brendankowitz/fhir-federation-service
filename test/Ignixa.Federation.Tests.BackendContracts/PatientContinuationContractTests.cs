namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Directional continuation behavior of a real Microsoft FHIR Server R4 on SQL, witnessed over FHIR HTTP.
/// It shares the seeded count backend, which holds exactly three synthetic Patients, with
/// <see cref="PatientCountContractTests"/>: no further SQL start and no write during the walk.
///
/// The expected order is not read from any search. Three direct reads of the reserved Patients give each
/// one's id, version and update instant, and the instants must be distinct; a tie is a failure of the
/// evidence, never a skip. A walk starts with <c>_sort=_lastUpdated</c> (or <c>-_lastUpdated</c>) and
/// <c>_count=1</c>, as a GET or as a form POST to <c>Patient/_search</c>, and follows each page's native
/// <c>next</c> link, replaying its query exactly with the same verb. Every issued link is redeemed twice
/// independently, and both redemptions must hold the one Patient the oracle expects at that position,
/// with the same version and instant. After the third Patient only an absent <c>next</c> or one empty
/// terminal page with no <c>next</c> is accepted. The walk is bounded to 15 requests plus the three
/// reads and sends nothing but to the local fixture origin and <c>Patient</c> path. It claims nothing
/// about tied instants, other queries, or the raw token text or Bundle bytes of any response.
/// </summary>
[Collection(LiveCountBackendDefinition.Name)]
public sealed class PatientContinuationContractTests(PatientCountBackendFixture backend, ITestOutputHelper output)
{
    private const int CaseTimeoutMilliseconds = 180_000;

    /// <summary>Contract. <c>GET Patient?_sort=_lastUpdated&amp;_count=1</c> and each GET next link, ascending.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Get_ascending_sort_walks_every_patient_by_instant_and_replays_each_next_link() =>
        AssertAsync(HttpMethod.Get, PatientSortDirection.Ascending, TestContext.Current.CancellationToken);

    /// <summary>Contract. <c>GET Patient?_sort=-_lastUpdated&amp;_count=1</c> and each GET next link, descending.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Get_descending_sort_walks_every_patient_by_instant_and_replays_each_next_link() =>
        AssertAsync(HttpMethod.Get, PatientSortDirection.Descending, TestContext.Current.CancellationToken);

    /// <summary>Contract. Form <c>POST Patient/_search</c> with <c>_sort=_lastUpdated</c> and a re-POST of each next link's query, ascending.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Post_ascending_sort_walks_every_patient_by_instant_and_replays_each_next_link() =>
        AssertAsync(HttpMethod.Post, PatientSortDirection.Ascending, TestContext.Current.CancellationToken);

    /// <summary>Contract. Form <c>POST Patient/_search</c> with <c>_sort=-_lastUpdated</c> and a re-POST of each next link's query, descending.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Post_descending_sort_walks_every_patient_by_instant_and_replays_each_next_link() =>
        AssertAsync(HttpMethod.Post, PatientSortDirection.Descending, TestContext.Current.CancellationToken);

    private async Task AssertAsync(HttpMethod method, PatientSortDirection direction, CancellationToken cancellationToken)
    {
        output.WriteLine($"Backend /metadata software: {backend.SoftwareIdentity}");
        ContinuationReport actual = await backend.WalkContinuationAsync(method, direction, cancellationToken);

        string evidence = actual.Describe($"{method.Method} Patient _sort {direction}");
        Assert.True(actual.Matched, evidence);
        output.WriteLine(evidence);
    }
}

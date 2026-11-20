namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Repeated decoded <c>_id</c> occurrences against the seeded Microsoft FHIR Server R4 on SQL, over FHIR
/// HTTP, as GET and as a form POST, for an ordinary primary page and for an ordinary <c>_count=0</c>.
/// It shares the seeded count backend with <see cref="PatientCountContractTests"/>: no further SQL start.
///
/// Every request sends the seeded id twice, <c>_id=count-female-1</c> and <c>%5Fid=%63ount-female-1</c>, which
/// decode to the same name and value. The page controls are <c>_count=100</c> with <c>_total=accurate</c> for
/// the primary and <c>_count=0</c> alone for the count; both send <c>Prefer: handling=strict</c>. Each case
/// reports two things apart. The match is a contract: the primary must hold exactly the seeded Patient with
/// total 1 on a complete page, and the count must report total 1 with no entries and no next link. The
/// <c>Bundle.self</c> echo is only a characterization: one equal occurrence is a collapsed pair, two is an
/// echoed pair, and either passes the case. A collapsed pair is not proof that the predicates were preserved,
/// and an echoed pair does not show that identical predicates independently affected the SQL result. A
/// response that cannot be accounted for fails as <c>[incomplete-search]</c>, and HTTP 400, 422 or 501 as
/// <c>[unsupported-capability]</c>, each naming the method and shape.
/// </summary>
[Collection(LiveCountBackendDefinition.Name)]
public sealed class RepeatedIdContractTests(PatientCountBackendFixture backend, ITestOutputHelper output)
{
    private const int CaseTimeoutMilliseconds = 120_000;

    /// <summary>Contract for the match, characterization for the echo. <c>GET Patient?_id=..&amp;_id=..</c>.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Get_primary_with_a_repeated_id_returns_the_seeded_patient_and_characterizes_the_echo() =>
        AssertAsync(HttpMethod.Get, RepeatedIdShape.Primary, TestContext.Current.CancellationToken);

    /// <summary>Contract for the match, characterization for the echo. <c>POST Patient/_search</c> with the pair in the form body.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Post_primary_with_a_repeated_id_returns_the_seeded_patient_and_characterizes_the_echo() =>
        AssertAsync(HttpMethod.Post, RepeatedIdShape.Primary, TestContext.Current.CancellationToken);

    /// <summary>Contract for the total, characterization for the echo. <c>GET Patient?_count=0</c> with the pair.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Get_count_with_a_repeated_id_reports_total_one_and_characterizes_the_echo() =>
        AssertAsync(HttpMethod.Get, RepeatedIdShape.Count, TestContext.Current.CancellationToken);

    /// <summary>Contract for the total, characterization for the echo. <c>POST Patient/_search</c> with <c>_count=0</c> and the pair.</summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public Task Post_count_with_a_repeated_id_reports_total_one_and_characterizes_the_echo() =>
        AssertAsync(HttpMethod.Post, RepeatedIdShape.Count, TestContext.Current.CancellationToken);

    private async Task AssertAsync(HttpMethod method, RepeatedIdShape shape, CancellationToken cancellationToken)
    {
        output.WriteLine($"Backend /metadata software: {backend.SoftwareIdentity}");
        RepeatedIdEvidence actual = await backend.SearchRepeatedIdAsync(method, shape, cancellationToken);

        string report = actual.Describe($"{method.Method} {(shape == RepeatedIdShape.Primary ? "primary" : "count")} repeated decoded _id");
        Assert.True(actual.ResultAsExpected, report);
        output.WriteLine(report);
    }
}

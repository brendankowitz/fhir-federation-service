namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// The Patient.given compatibility witness against the seeded Microsoft FHIR Server R4 on SQL, over FHIR
/// HTTP, as GET and as a form POST to <c>Patient/_search</c>, for six cases: one value, a second value, the
/// AND of two repeated parameters, the OR of a comma list, and <c>:exact</c> in two spellings of case.
///
/// Every request sends its selection text literally, then <c>_count=100&amp;_total=accurate</c>, with
/// <c>Prefer: handling=strict</c>. Each case is a contract on two things apart: the sorted match ids of one
/// complete page must equal the literal expectation, and <c>Bundle.self</c> must echo exactly the decoded
/// selection pairs with their multiplicity, apart from the page controls. A response that cannot be
/// accounted for fails as <c>[incomplete-search]</c>, and HTTP 400, 422 or 501 as
/// <c>[unsupported-capability]</c>, each naming the method and case. The live outcome of this suite is not
/// recorded by the repository until a Linux Docker run reports it.
/// </summary>
[Collection(LiveGivenBackendDefinition.Name)]
public sealed class PatientGivenContractTests(PatientGivenBackendFixture backend, ITestOutputHelper output)
{
    private const int CaseTimeoutMilliseconds = 120_000;

    /// <summary>Case labels; <see cref="GivenCases"/> holds the literal request and expectations.</summary>
    [Theory(Timeout = CaseTimeoutMilliseconds)]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("exact")]
    [InlineData("exact-upper")]
    public Task Get_Patient_given_returns_the_literal_matches_and_echoes_the_selection(string label) =>
        AssertAsync(HttpMethod.Get, label, TestContext.Current.CancellationToken);

    /// <summary>The same six cases as a form body to <c>POST Patient/_search</c>.</summary>
    [Theory(Timeout = CaseTimeoutMilliseconds)]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("exact")]
    [InlineData("exact-upper")]
    public Task Post_Patient_given_returns_the_literal_matches_and_echoes_the_selection(string label) =>
        AssertAsync(HttpMethod.Post, label, TestContext.Current.CancellationToken);

    private async Task AssertAsync(HttpMethod method, string label, CancellationToken cancellationToken)
    {
        GivenCase given = GivenCases.For(label);
        output.WriteLine($"Backend /metadata software: {backend.SoftwareIdentity}");
        GivenEvidence actual = await backend.SearchGivenAsync(method, given, cancellationToken);

        string report = actual.Describe($"{method.Method} {label} Patient.given", given);
        Assert.True(actual.IsAsExpected(given), report);
        output.WriteLine(report);
    }
}

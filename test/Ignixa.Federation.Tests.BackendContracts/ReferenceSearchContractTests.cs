namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Reference-search behavior of a real Microsoft FHIR Server R4 on SQL, witnessed over FHIR HTTP.
/// Design §4.1 says a relative reference means "on the referencing shard" and an absolute
/// reference names exactly one shard. §7.5 (Distributed rewrite) and §7.6 (revinclude) therefore
/// need each shard to answer two exact queries: one for a foreign key (<c>subject=https://X/Patient/a</c>)
/// and one for its own keys that matches relative and own-base references but no foreign reference.
///
/// A profile certifies the second query as exact-local: the backend must return relative and
/// own-absolute references to the id and nothing else. Case 1 is that requirement, spelled as a
/// host-qualified query for the backend's own base. The relative spelling <c>subject=Patient/dup</c>
/// is not a way to satisfy it. Case 3 only characterizes it: it ignores the base, so it also admits
/// foreign references with the same type and id.
///
/// One seeded backend serves three independently reported cases. Every expected id list is a
/// literal in this file, written from the requirement and the upstream source, never computed from
/// the seed code. Seeded data (see README):
/// <list type="bullet">
/// <item><c>obs-relative-dup</c>: subject <c>Patient/dup</c></item>
/// <item><c>obs-own-absolute-dup</c>: subject <c>{local request base}/Patient/dup</c></item>
/// <item><c>obs-foreign-absolute-dup</c> (foreign A): subject <c>https://foreign-shard.invalid/Patient/dup</c></item>
/// <item><c>obs-foreign-b-absolute-dup</c> (foreign B): subject <c>https://foreign-shard-b.invalid/Patient/dup</c>,
/// the same as foreign A except for its host</item>
/// <item><c>obs-control-other</c>: subject <c>Patient/other</c></item>
/// </list>
/// </summary>
public sealed class ReferenceSearchContractTests(ReferenceBackendFixture backend, ITestOutputHelper output) : IClassFixture<ReferenceBackendFixture>
{
    private const int CaseTimeoutMilliseconds = 120_000;

    /// <summary>
    /// The query for foreign A. The live case and the offline control in
    /// <see cref="BackendEvidenceTests"/> both judge their results with
    /// <see cref="CompareForeignAQuery"/>, so the expected set cannot be relaxed in one place only.
    /// </summary>
    private const string ForeignAQuery = "https://foreign-shard.invalid/Patient/dup";

    /// <summary>Only foreign A. Foreign B has the same type and id and a different host, so it must not match.</summary>
    private static readonly string[] ForeignAQueryExpected = ["obs-foreign-absolute-dup"];

    internal static MatchComparison CompareForeignAQuery(SearchsetEvidence actual) =>
        MatchComparison.Compare(EvidenceLabel.Contract, "subject=<foreign A base>/Patient/dup", ForeignAQueryExpected, actual);

    /// <summary>
    /// Contract. A host-qualified query for the backend's own base must match the relative reference
    /// and the own-absolute reference, because both name the same local resource, and must not match
    /// either foreign reference, which has the same type and id.
    /// Breaks if the backend treats host-qualified queries as type/id-only (foreign admitted) or
    /// compares the reference literally (relative reference excluded).
    /// </summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public async Task Contract_own_absolute_query_matches_relative_and_own_absolute_but_not_foreign()
    {
        string[] expected = ["obs-own-absolute-dup", "obs-relative-dup"];

        SearchsetEvidence actual = await SearchAsync($"{backend.LocalBaseUrl}Patient/dup", TestContext.Current.CancellationToken);

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Contract, "subject=<own base>/Patient/dup", expected, actual);
        AssertMatches(comparison);
    }

    /// <summary>
    /// Contract. A host-qualified query for foreign A must match only the reference that carries that
    /// host. The local references and foreign B, which differs only in host, must not match.
    /// </summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public async Task Contract_foreign_absolute_query_matches_only_the_foreign_reference()
    {
        SearchsetEvidence actual = await SearchAsync(ForeignAQuery, TestContext.Current.CancellationToken);

        AssertMatches(CompareForeignAQuery(actual));
    }

    /// <summary>
    /// Characterization, not a federation contract. A type/id query without a base matches every
    /// reference to <c>Patient/dup</c>, whatever its base. Upstream release/5.2.84 source says so:
    /// SearchValueExpressionBuilderHelper adds no base-URI condition for a value without a base, but
    /// requires a missing base for a value that names the server's own base. This case records why
    /// <c>subject=Patient/dup</c> is not an exact-local query: it admits both foreign references
    /// alongside the local ones.
    /// </summary>
    [Fact(Timeout = CaseTimeoutMilliseconds)]
    public async Task Characterization_type_and_id_query_matches_every_same_id_reference()
    {
        string[] expected =
        [
            "obs-foreign-absolute-dup", "obs-foreign-b-absolute-dup", "obs-own-absolute-dup", "obs-relative-dup",
        ];

        SearchsetEvidence actual = await SearchAsync("Patient/dup", TestContext.Current.CancellationToken);

        MatchComparison comparison = MatchComparison.Compare(
            EvidenceLabel.Characterization, "subject=Patient/dup", expected, actual);
        AssertMatches(comparison);
    }

    private Task<SearchsetEvidence> SearchAsync(string subject, CancellationToken cancellationToken)
    {
        output.WriteLine($"Backend /metadata software: {backend.SoftwareIdentity}");
        return backend.SearchObservationsBySubjectAsync(subject, cancellationToken);
    }

    private void AssertMatches(MatchComparison comparison)
    {
        string evidence = comparison.Describe();
        Assert.True(comparison.IsEqual, evidence);
        output.WriteLine(evidence);
    }
}

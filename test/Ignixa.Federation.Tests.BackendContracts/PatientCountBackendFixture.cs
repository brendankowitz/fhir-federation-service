namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Shares one disposable Microsoft FHIR Server on SQL, seeded with only the synthetic count Patients,
/// across the ordinary-count cases. It owns its own instance, apart from <see cref="ReferenceBackendFixture"/>.
/// xUnit does not call <see cref="DisposeAsync"/> when <see cref="InitializeAsync"/> throws, so the
/// backend removes its own resources before it fails.
/// </summary>
public sealed class PatientCountBackendFixture : IAsyncLifetime
{
    private MicrosoftFhirSqlBackend? _backend;

    public string SoftwareIdentity => Backend.SoftwareIdentity;

    private MicrosoftFhirSqlBackend Backend =>
        _backend ?? throw new InvalidOperationException("The backend fixture was not initialized.");

    public async ValueTask InitializeAsync() =>
        _backend = await MicrosoftFhirSqlBackend.StartAsync(CountSeedData.Seed, TestContext.Current.CancellationToken);

    /// <summary>Runs the ordinary <c>_count=0</c> Patient request and returns the accounted-for total.</summary>
    internal Task<CountEvidence> CountPatientsAsync(HttpMethod method, string? gender, CancellationToken cancellationToken) =>
        Backend.Client.CountPatientsAsync(method, gender, cancellationToken);

    /// <summary>Runs the repeated decoded <c>_id</c> request for the seeded Patient and returns the accounted-for answer.</summary>
    internal Task<RepeatedIdEvidence> SearchRepeatedIdAsync(HttpMethod method, RepeatedIdShape shape, CancellationToken cancellationToken) =>
        Backend.Client.SearchRepeatedIdAsync(method, shape, cancellationToken);

    /// <summary>
    /// Walks the sorted Patient search through its native continuations against the direct-read oracle of the
    /// reserved Patients and returns the accounted-for report.
    /// </summary>
    internal Task<ContinuationReport> WalkContinuationAsync(HttpMethod method, PatientSortDirection direction, CancellationToken cancellationToken) =>
        ContinuationWitness.RunAsync(Backend.Client, CountSeedData.Seed.Ids, method, direction, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }
}

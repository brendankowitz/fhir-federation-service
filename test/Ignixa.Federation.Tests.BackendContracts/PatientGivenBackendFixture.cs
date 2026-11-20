namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Shares one disposable Microsoft FHIR Server on SQL, seeded with only the six synthetic Patient.given
/// Patients, across the Patient.given cases. It owns its own instance, apart from
/// <see cref="ReferenceBackendFixture"/> and <see cref="PatientCountBackendFixture"/>, so the count and
/// exact-id answers never depend on this seed. xUnit does not call <see cref="DisposeAsync"/> when
/// <see cref="InitializeAsync"/> throws, so the backend removes its own resources before it fails.
/// </summary>
public sealed class PatientGivenBackendFixture : IAsyncLifetime
{
    private MicrosoftFhirSqlBackend? _backend;

    public string SoftwareIdentity => Backend.SoftwareIdentity;

    private MicrosoftFhirSqlBackend Backend =>
        _backend ?? throw new InvalidOperationException("The backend fixture was not initialized.");

    public async ValueTask InitializeAsync() =>
        _backend = await MicrosoftFhirSqlBackend.StartAsync(GivenSeedData.Seed, TestContext.Current.CancellationToken);

    /// <summary>Runs one Patient.given request and returns the accounted-for answer.</summary>
    internal Task<GivenEvidence> SearchGivenAsync(HttpMethod method, GivenCase given, CancellationToken cancellationToken) =>
        Backend.Client.SearchGivenAsync(method, given, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }
}

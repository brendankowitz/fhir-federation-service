namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Shares one backend across the three reference cases. xUnit does not call <see cref="DisposeAsync"/>
/// when <see cref="InitializeAsync"/> throws, so the backend removes its own resources before it fails.
/// </summary>
public sealed class ReferenceBackendFixture : IAsyncLifetime
{
    private MicrosoftFhirSqlBackend? _backend;

    /// <summary>The base the test client uses against the backend, with a trailing slash.</summary>
    internal string LocalBaseUrl => Backend.LocalBaseUrl;
    public string SoftwareIdentity => Backend.SoftwareIdentity;

    private MicrosoftFhirSqlBackend Backend =>
        _backend ?? throw new InvalidOperationException("The backend fixture was not initialized.");

    public async ValueTask InitializeAsync() =>
        _backend = await MicrosoftFhirSqlBackend.StartAsync(SeedData.Reference, TestContext.Current.CancellationToken);

    /// <summary>Runs <c>Observation?subject={subjectValue}</c> and returns the accounted-for matches.</summary>
    public Task<SearchsetEvidence> SearchObservationsBySubjectAsync(string subjectValue, CancellationToken cancellationToken) =>
        Backend.Client.SearchObservationsAsync($"subject={Uri.EscapeDataString(subjectValue)}", cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }
}

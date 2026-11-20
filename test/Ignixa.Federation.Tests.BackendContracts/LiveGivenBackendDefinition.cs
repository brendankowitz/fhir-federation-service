namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Reserves the live SQL backend for the Patient.given class and shares its seeded backend across that
/// class's twelve cases. xUnit runs a collection that disables parallelization after the parallel ones
/// finish, so this fixture's SQL Server and FHIR server never start while another fixture's are running.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveGivenBackendDefinition : ICollectionFixture<PatientGivenBackendFixture>
{
    public const string Name = "Live Patient.given backend";
}

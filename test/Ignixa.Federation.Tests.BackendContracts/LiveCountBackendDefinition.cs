namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Reserves the live SQL backend for one class at a time and shares the seeded count backend between the
/// classes of this collection. xUnit runs a collection that disables parallelization after the parallel
/// ones finish, so the count fixture's SQL Server and FHIR server never start while the reference
/// fixture's are running. Both Patient test classes reuse one startup.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCountBackendDefinition : ICollectionFixture<PatientCountBackendFixture>
{
    public const string Name = "Live count backend";
}

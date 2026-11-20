namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// Reserves the live SQL backend for one class at a time. xUnit runs a collection that disables
/// parallelization after the parallel ones finish, so the count fixture's SQL Server and FHIR server
/// never start while the reference fixture's are running.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCountBackendDefinition
{
    public const string Name = "Live count backend";
}

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>
/// What a disposable backend is seeded with and how readiness is judged: a positive <c>_id</c> page
/// of <see cref="Ids"/> must return every seeded resource of <see cref="ResourceType"/>.
/// </summary>
/// <param name="ResourceType">The type the readiness search runs against.</param>
/// <param name="Ids">The ids of the seeded resources of that type.</param>
/// <param name="Resources">The resources to write in write order, given the local base with a trailing slash.</param>
internal sealed record BackendSeed(
    string ResourceType,
    IReadOnlyList<string> Ids,
    Func<string, IReadOnlyList<SeedResource>> Resources);

using Ignixa.Federation.Shards.Capabilities;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>Real evidence values that differ by the one resource type they advertise, so a test can tell them apart.</summary>
internal static class RefreshEvidence
{
    public static ShardCapabilityEvidence Advertising(string resourceType) =>
        CapabilityJson.Read(CapabilityJson.ServerStatement(CapabilityJson.Arr(CapabilityJson.SearchableResource(resourceType))));

    /// <summary>Distinct evidence for every shard of one sweep.</summary>
    public static object[] Sweep(string resourceType) => [Advertising(resourceType), Advertising(resourceType), Advertising(resourceType)];

    public static string[] ResourceTypes(ShardCapabilityEvidence evidence) =>
        [.. evidence.SearchableResources.Keys.Select(t => t.Value).Order(StringComparer.Ordinal)];
}

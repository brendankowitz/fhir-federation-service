using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>Builds shards, streams, entries and pages for executor tests. Shard <c>sN</c> has ordinal N.</summary>
internal static class Fixture
{
    /// <summary>A parameter value that must never appear in an exception message.</summary>
    public const string SecretValue = "sarah-secret";

    public static readonly ResourceType Patient = ResourceType.Parse("Patient");

    public static readonly ResourceType Observation = ResourceType.Parse("Observation");

    /// <summary>A backend that honours at most two results per page.</summary>
    public static readonly BackendProfile SmallPages = new("SmallPages", SupportsPostSearch: true, MaxCount: 2, NextLinkRedemption.RepostToSearch, null);

    /// <summary>Creates shards s0..s(count-1); the listed ordinals use <see cref="SmallPages"/>.</summary>
    public static ShardRegistry Registry(int count, params int[] smallPageShards)
    {
        var shards = Enumerable.Range(0, count)
            .Select(i => TestShards.Shard($"s{i}", $"a{i}", $"https://s{i}.example/fhir")
                .WithProfile(smallPageShards.Contains(i) ? SmallPages.Name : TestShards.MicrosoftFhirServer.Name))
            .ToArray();
        return ShardRegistry.Create(shards, [TestShards.MicrosoftFhirServer, SmallPages]);
    }

    public static ShardId Shard(int ordinal) => ShardId.Parse($"s{ordinal}");

    public static ShardSearchRequest Stream(int ordinal, ShardSearchMethod method = ShardSearchMethod.Get, ResourceType? type = null) =>
        new(Shard(ordinal), type ?? Patient, [new ParameterOccurrence("name", SecretValue)], 20, method);

    public static ShardSearchRequest[] Streams(int count) => [.. Enumerable.Range(0, count).Select(i => Stream(i))];

    public static ResourceKey Key(int ordinal, string id, ResourceType? type = null) => Key(Shard(ordinal), id, type);

    public static ResourceKey Key(ShardId shard, string id, ResourceType? type = null) => new(shard, type ?? Patient, ResourceId.Parse(id));

    public static ShardEntry Entry(ResourceKey key) => new(key, "1", null, "{\"resourceType\":\"Patient\"}"u8);

    public static ShardEntry[] Entries(int ordinal, params string[] ids) => [.. ids.Select(id => Entry(Key(ordinal, id)))];

    public static ShardCursor Cursor(
        int ordinal,
        string target,
        NextLinkRedemption redemption = NextLinkRedemption.FollowGet,
        ResourceType? type = null) =>
        new(Shard(ordinal), type ?? Patient, $"https://s{ordinal}.example/fhir/Patient?ct={target}&name={SecretValue}", redemption);

    public static ShardPage Page(ShardCursor? next, params ShardEntry[] entries) => new(entries, next, null);

    public static UnsortedPageExecutor Executor(IShardClient client, ShardRegistry registry, int maxParallelFetches = 1) =>
        new(client, registry, maxParallelFetches);

    /// <summary>Renders the page's matches as <c>shard/id</c>, in emission order.</summary>
    public static string[] Ids(this UnsortedPage page) => [.. page.Matches.Select(m => $"{m.Key.Shard}/{m.Key.Id}")];

    public static string[] Ids(int ordinal, params string[] ids) => [.. ids.Select(id => $"s{ordinal}/{id}")];
}

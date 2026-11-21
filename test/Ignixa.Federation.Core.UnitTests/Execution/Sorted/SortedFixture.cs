using System.Text;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>
/// Builds sorted streams, timestamped entries and scripted page chains for sorted-executor tests. Shard <c>sN</c> has
/// ordinal N, as in <see cref="Fixture"/>.
/// </summary>
internal static class SortedFixture
{
    /// <summary>The instant every <see cref="At"/> offset counts from.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(int seconds) => Epoch.AddSeconds(seconds);

    /// <summary>The kept <c>_sort</c> occurrence the planner leaves in a sorted stream's request.</summary>
    public static ParameterOccurrence SortOccurrence(LastUpdatedSort direction) =>
        new("_sort", direction == LastUpdatedSort.Ascending ? "_lastUpdated" : "-_lastUpdated");

    public static ShardSearchRequest Stream(
        int ordinal,
        LastUpdatedSort direction = LastUpdatedSort.Ascending,
        ShardSearchMethod method = ShardSearchMethod.Get,
        ResourceType? type = null,
        int pageSize = 20) =>
        new(
            Fixture.Shard(ordinal),
            type ?? Fixture.Patient,
            [new ParameterOccurrence("name", Fixture.SecretValue), SortOccurrence(direction)],
            pageSize,
            method);

    public static ShardSearchRequest[] Streams(LastUpdatedSort direction, params int[] ordinals) =>
        [.. ordinals.Select(ordinal => Stream(ordinal, direction))];

    /// <summary>Creates an entry whose version and bytes are distinct per shard and id.</summary>
    public static ShardEntry Entry(int ordinal, string id, DateTimeOffset? lastUpdated) =>
        new(
            Fixture.Key(ordinal, id),
            $"v-s{ordinal}-{id}",
            lastUpdated,
            Encoding.UTF8.GetBytes($"{{\"resourceType\":\"Patient\",\"id\":\"{id}\",\"meta\":{{\"source\":\"s{ordinal}\"}}}}"));

    public static ShardEntry Entry(int ordinal, string id, int seconds) => Entry(ordinal, id, At(seconds));

    public static SortedPageExecutor Executor(IShardClient client, ShardRegistry registry, int maxParallelFetches = 1) =>
        new(client, registry, maxParallelFetches);

    /// <summary>
    /// Scripts a chain of backend pages for one shard: its first search returns <paramref name="pages"/>[0], and each page
    /// links to the next with a cursor; the last page has no <c>next</c>.
    /// </summary>
    /// <returns>The cursors, where cursor i returns page i + 1.</returns>
    public static ShardCursor[] Chain(this FakeShardClient client, int ordinal, params ShardEntry[][] pages)
    {
        ShardCursor[] cursors = [.. Enumerable.Range(0, pages.Length - 1).Select(i => Fixture.Cursor(ordinal, $"p{i + 1}"))];
        client.OnSearch(ordinal, Fixture.Page(cursors.ElementAtOrDefault(0), pages[0]));
        for (int i = 0; i < cursors.Length; i++)
        {
            client.OnContinue(cursors[i], Fixture.Page(cursors.ElementAtOrDefault(i + 1), pages[i + 1]));
        }

        return cursors;
    }

    /// <summary>Renders the page's matches as <c>shard/id</c>, in emission order.</summary>
    public static string[] Ids(this SortedPage page) => [.. page.Matches.Select(m => $"{m.Key.Shard}/{m.Key.Id}")];
}

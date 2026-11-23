using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ignixa.Federation.Core.Execution;
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

    /// <summary>Creates the anchor of <see cref="Entry(int, string, int)"/>.</summary>
    public static SortedAnchor Anchor(int ordinal, string id, int seconds, ResourceType? type = null) =>
        new(At(seconds), Fixture.Key(ordinal, id, type));

    public static SortedPageExecutor Executor(IShardClient client, ShardRegistry registry, int maxParallelFetches = 1) =>
        new(client, registry, maxParallelFetches);

    /// <summary>
    /// Redeems a hand-built snapshot whose owners are shards s0, s1, … in registry order, on a fresh executor with a budget
    /// of 10.
    /// </summary>
    public static Task<SortedPage> Resume(
        this FakeShardClient client,
        LastUpdatedSort direction,
        int pageSize,
        params SortedStreamContinuation[] owners)
    {
        int[] ordinals = [.. Enumerable.Range(0, owners.Length)];
        var resumeFrom = new SortedContinuation(Fixture.Patient, direction, pageSize, owners);
        return Executor(client, Fixture.Registry(owners.Length))
            .ExecuteAsync(Streams(direction, ordinals), direction, pageSize, resumeFrom, 10, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads federation pages from the first, redeeming each <c>Next</c> with a budget of 10, until a page has none.
    /// </summary>
    /// <exception cref="InvalidOperationException">More than <paramref name="maxPages"/> pages would be read.</exception>
    public static async Task<List<SortedPage>> ReadAllAsync(
        this SortedPageExecutor executor,
        ShardSearchRequest[] streams,
        LastUpdatedSort direction,
        int pageSize,
        int maxPages = 50)
    {
        var pages = new List<SortedPage>();
        SortedContinuation? next = null;
        do
        {
            if (pages.Count == maxPages)
            {
                throw new InvalidOperationException($"The chain did not end within {maxPages} pages.");
            }

            SortedPage page = await executor.ExecuteAsync(streams, direction, pageSize, next, 10, TestContext.Current.CancellationToken);
            pages.Add(page);
            next = page.Next;
        }
        while (next is not null);

        return pages;
    }

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

    /// <summary>
    /// Projects a checkpoint as text: <c>s0 search count=3 a@1</c>, <c>s0 cursor p1 b@3 power=2 steps=0 preceding=1</c> or
    /// <c>s1 end d@2</c>, where <c>id@n</c> is the anchor at <see cref="At"/>(n) and <c>-</c> means none.
    /// </summary>
    public static string Describe(this SortedStreamContinuation stream)
    {
        string anchor = stream.LastEmitted is { } emitted ? $"{emitted.Key.Id}@{Seconds(emitted.LastUpdated)}" : "-";
        if (stream.IsExhausted)
        {
            return $"{stream.Shard} end {anchor}";
        }

        if (stream.Search is { } search)
        {
            return $"{stream.Shard} search count={search.PageSize} {anchor}";
        }

        string target = Regex.Match(stream.Cursor!.Target, "ct=([^&]+)").Groups[1].Value;
        string preceding = stream.PreviousPageLastUpdated is { } instant ? Seconds(instant) : "-";
        CursorCycleState cycle = stream.CycleState;
        return $"{stream.Shard} cursor {target} {anchor} power={cycle.Power} steps={cycle.Steps} preceding={preceding}";
    }

    public static string[] Describe(this SortedContinuation continuation) => [.. continuation.Streams.Select(Describe)];

    private static string Seconds(DateTimeOffset instant) =>
        (instant - Epoch).TotalSeconds.ToString(CultureInfo.InvariantCulture);
}

using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Execution.Sorted;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;

namespace Ignixa.Federation.Core.UnitTests.Execution.Sorted;

/// <summary>
/// How a sorted execution admits, joins and arbitrates shard calls (design Q7, Q9; tests 27–33). Gates order every
/// completion, so no outcome depends on timing.
/// </summary>
public sealed class SortedPageExecutorSchedulingTests
{
    private const LastUpdatedSort Ascending = LastUpdatedSort.Ascending;

    /// <summary>Bounds a wait that only an executor admitting the wrong calls would never finish.</summary>
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four gated owners are primed in ordinal batches of at most <paramref name="maxParallel"/>. Each batch is released
    /// against ordinal order; the next batch may start only once the whole batch has finished.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task PrimingBatches_ReachButNeverExceedTheConcurrency_WhateverOrderTheyComplete(int maxParallel)
    {
        var client = new FakeShardClient();
        TaskCompletionSource[] gates = [.. Enumerable.Range(0, 4).Select(_ => Gate())];
        for (int i = 0; i < 4; i++)
        {
            client.OnSearch(i, gates[i], Fixture.Page(null, SortedFixture.Entry(i, "a", 10 - i), SortedFixture.Entry(i, "b", 20 - i)));
        }

        Task<SortedPage> running = SortedFixture.Executor(client, Fixture.Registry(4), maxParallel)
            .ExecuteAsync(SortedFixture.Streams(Ascending, 3, 1, 0, 2), Ascending, 5, null, 10, Token);
        for (int started = 0; started < 4;)
        {
            int batch = Math.Min(maxParallel, 4 - started);
            await client.WhenStarted(started + batch).WaitAsync(Guard, Token);
            client.Calls.Count.ShouldBe(started + batch, "a later batch waits for this one");
            for (int i = started + batch - 1; i >= started; i--)
            {
                gates[i].SetResult();
                await client.Calls[i].Finished.WaitAsync(Token);
            }

            started += batch;
        }

        SortedPage page = await running;

        client.MaxInFlight.ShouldBe(maxParallel);
        client.Calls.Select(c => c.ToString()).ShouldBe(["search s0 count=5", "search s1 count=5", "search s2 count=5", "search s3 count=5"]);
        page.Ids().ShouldBe(["s3/a", "s2/a", "s1/a", "s0/a", "s3/b"]);
        page.Fetches.ShouldBe(4);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

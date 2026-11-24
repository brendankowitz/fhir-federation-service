using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// The scheduling policy of <see cref="ShardCapabilityRefresher"/>: when sweeps start, that they never overlap, and how
/// stopping ends them. The deadline and byte limit of one fetch belong to the real fetch contract and are tested where
/// that contract lives (<c>HttpShardClientMetadataTests</c>); here the refresher must add none of its own.
/// </summary>
public sealed class ShardCapabilityRefresherScheduleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Millisecond = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task StartupSweepsAtOnceOneShardAtATimeInRegistryOrderAndPublishesEachResultAsItEnds()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence eastEvidence = RefreshEvidence.Advertising("Patient");
        ShardCapabilitySnapshot beforeStart = rig.Refresher.Current;

        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        east.Shard.ShouldBeSameAs(rig.East);
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("a second fetch started while the first was in flight");
        rig.Refresher.Current.ShouldBeSameAs(beforeStart);

        east.Succeed(eastEvidence);
        PendingFetch west = await rig.Script.NextAsync();

        west.Shard.ShouldBeSameAs(rig.West);
        rig.Refresher.Current.Shards[rig.East.Id].Evidence.ShouldBeSameAs(eastEvidence);
        rig.Refresher.Current.Shards[rig.West.Id].IsIncomplete.ShouldBeTrue();
        beforeStart.Shards[rig.East.Id].Evidence.ShouldBeNull("a published snapshot must never change");
    }

    [Fact]
    public async Task TheNextSweepIsDueTenMinutesAfterStartupNotAfterTheLastSweepFinished()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        PendingFetch firstEast = await rig.Script.NextAsync();

        // The startup sweep ends three minutes in; the next one is still due ten minutes after startup.
        rig.Time.Advance(TimeSpan.FromMinutes(3));
        await rig.SweepAsync(firstEast, RefreshEvidence.Sweep("Patient"));

        rig.Time.Advance(Interval - TimeSpan.FromMinutes(3) - Millisecond);
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("a sweep started before the interval elapsed");

        rig.Time.Advance(Millisecond);
        (await rig.Script.NextAsync()).Shard.ShouldBeSameAs(rig.East);
    }

    [Fact]
    public async Task ASweepThatOutlastsTheIntervalDoesNotOverlapAndMissedTicksCoalesceIntoOneFollowUp()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        rig.Time.Advance(TimeSpan.FromMinutes(35)); // ticks due at 10, 20 and 30 minutes
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("a sweep started while another was running");

        await rig.SweepAsync(east, RefreshEvidence.Sweep("Patient"));

        // One follow-up sweep, not three.
        await rig.SweepAsync(RefreshEvidence.Sweep("Organization"));
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("missed ticks were replayed instead of coalesced");

        // The clock was never reset: the next tick is the one due at 40 minutes.
        rig.Time.Advance(TimeSpan.FromMinutes(5) - Millisecond);
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse();
        rig.Time.Advance(Millisecond);
        (await rig.Script.NextAsync()).Shard.ShouldBeSameAs(rig.East);
    }

    [Fact]
    public async Task TheRefresherAddsNoDeadlineOfItsOwnToAFetch()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        rig.Time.Advance(TimeSpan.FromHours(1));
        await Watchdog.SettleAsync();

        east.Token.IsCancellationRequested.ShouldBeFalse();
        rig.Run.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task ASecondStartWhileRunningIsRefusedAndLeavesTheFirstLoopRunning()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            rig.Refresher.RunAsync(TestContext.Current.CancellationToken).WaitAsync(Watchdog.Interval, TestContext.Current.CancellationToken));

        east.Succeed(RefreshEvidence.Advertising("Patient"));
        (await rig.Script.NextAsync()).Shard.ShouldBeSameAs(rig.West);
        await Watchdog.SettleAsync();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("the refused start must not have started a second loop");
    }

    [Fact]
    public async Task CancellationDuringAFetchEndsNormallyWithoutManufacturingAFailure()
    {
        await using var rig = new RefreshRig();
        ShardCapabilitySnapshot before = rig.Refresher.Current;
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        rig.Stop();
        await rig.EndedAsync();

        east.Token.IsCancellationRequested.ShouldBeTrue();
        rig.Run.IsCompletedSuccessfully.ShouldBeTrue();
        rig.Refresher.Current.ShouldBeSameAs(before);
        rig.Refresher.Current.Shards.Values.ShouldAllBe(e => e.Failure == null && e.IsIncomplete);
    }

    [Fact]
    public async Task ALateResultAfterCancellationIsNotPublished()
    {
        await using var rig = new RefreshRig(honorsCancellation: false);
        ShardCapabilitySnapshot before = rig.Refresher.Current;
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        rig.Stop();
        east.Succeed(RefreshEvidence.Advertising("Patient"));
        await rig.EndedAsync();

        rig.Run.IsCompletedSuccessfully.ShouldBeTrue();
        rig.Refresher.Current.ShouldBeSameAs(before);
        rig.Script.TryTakeStarted(out _).ShouldBeFalse("no further shard is fetched after stopping");
    }

    [Fact]
    public async Task CancellationWhileWaitingForTheNextTickEndsNormallyAndNothingFurtherIsFetched()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        await rig.SweepAsync(RefreshEvidence.Sweep("Patient"));
        ShardCapabilitySnapshot published = rig.Refresher.Current;

        rig.Stop();
        await rig.EndedAsync();
        rig.Time.Advance(3 * Interval);
        await Watchdog.SettleAsync();

        rig.Run.IsCompletedSuccessfully.ShouldBeTrue();
        rig.Refresher.Current.ShouldBeSameAs(published);
        rig.Script.TryTakeStarted(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AlreadyCancelledFetchesNothing()
    {
        await using var rig = new RefreshRig();
        rig.Stop();

        rig.Start();
        await rig.EndedAsync();

        rig.Run.IsCompletedSuccessfully.ShouldBeTrue();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AProgrammingExceptionFromTheSeamFaultsTheLoopAndIsNotRecordedAsAShardFailure()
    {
        await using var rig = new RefreshRig();
        ShardCapabilitySnapshot before = rig.Refresher.Current;
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        east.Fail(new InvalidOperationException("bug in the seam"));

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(rig.EndedAsync());
        thrown.Message.ShouldBe("bug in the seam");
        rig.Refresher.Current.ShouldBeSameAs(before);
    }

    [Fact]
    public async Task ACancellationThatTheStopTokenDidNotRequestIsNotMaskedAsShutdown()
    {
        await using var rig = new RefreshRig();
        rig.Start();
        PendingFetch east = await rig.Script.NextAsync();

        east.Fail(new OperationCanceledException("not ours"));

        await Should.ThrowAsync<OperationCanceledException>(rig.EndedAsync());
    }
}

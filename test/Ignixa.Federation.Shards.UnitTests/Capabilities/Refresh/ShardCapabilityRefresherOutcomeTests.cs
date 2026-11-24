using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>What <see cref="ShardCapabilityRefresher"/> publishes: the initial state, and how each fetch outcome changes it.</summary>
public sealed class ShardCapabilityRefresherOutcomeTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public static TheoryData<ShardCallFailure> Failures => new(Enum.GetValues<ShardCallFailure>());

    [Fact]
    public async Task BeforeTheLoopRunsEveryRegisteredShardHasOnePendingEntry()
    {
        await using var rig = new RefreshRig();

        ShardCapabilitySnapshot pending = rig.Refresher.Current;
        IReadOnlyDictionary<ShardId, ShardCapabilityEntry> shards = pending.Shards;

        shards.Keys.ShouldBe(rig.Registry.Shards.Select(s => s.Id), ignoreOrder: true);
        shards.Values.ShouldAllBe(e => e.Evidence == null && e.Failure == null && e.IsIncomplete && !e.IsDegraded);
        pending.IsComplete.ShouldBeFalse();
        pending.IsDegraded.ShouldBeFalse();
    }

    [Fact]
    public async Task ACompleteSnapshotCanBeDegradedAndAnIncompleteSnapshotCanRetainDegradedEvidence()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence healthy = RefreshEvidence.Advertising("Patient");
        var noWestEvidence = new ShardCallException(rig.West, ShardCallFailure.Timeout, "deadline");
        rig.Start();
        await rig.SweepAsync(healthy, noWestEvidence, healthy);
        ShardCapabilitySnapshot incomplete = rig.Refresher.Current;

        rig.Time.Advance(Interval);
        var staleEast = new ShardCallException(rig.East, ShardCallFailure.Transport, "wire");
        await rig.SweepAsync(staleEast, noWestEvidence, healthy);
        ShardCapabilitySnapshot incompleteAndDegraded = rig.Refresher.Current;

        rig.Time.Advance(Interval);
        await rig.SweepAsync(staleEast, healthy, healthy);
        ShardCapabilitySnapshot completeAndDegraded = rig.Refresher.Current;

        (incomplete.IsComplete, incomplete.IsDegraded).ShouldBe((false, false));
        (incompleteAndDegraded.IsComplete, incompleteAndDegraded.IsDegraded).ShouldBe((false, true));
        (completeAndDegraded.IsComplete, completeAndDegraded.IsDegraded).ShouldBe((true, true));
    }

    [Fact]
    public async Task AShardThatNeverAnsweredIsIncompleteEvenAfterFailureWhileAHealthyShardIsComplete()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence healthy = RefreshEvidence.Advertising("Patient");
        ShardCapabilityEvidence other = RefreshEvidence.Advertising("Observation");
        rig.Start();

        await rig.SweepAsync(healthy, new ShardCallException(rig.West, ShardCallFailure.Timeout, "deadline"), other);

        ShardCapabilityEntry east = rig.Refresher.Current.Shards[rig.East.Id];
        east.Evidence.ShouldBeSameAs(healthy);
        RefreshEvidence.ResourceTypes(east.Evidence!).ShouldBe(["Patient"]);
        (east.Failure, east.IsIncomplete, east.IsDegraded).ShouldBe((null, false, false));

        ShardCapabilityEntry west = rig.Refresher.Current.Shards[rig.West.Id];
        (west.Evidence, west.Failure, west.IsIncomplete, west.IsDegraded).ShouldBe((null, ShardCallFailure.Timeout, true, false));

        // The failure did not stop the sweep: the shard after it was fetched and published.
        rig.Refresher.Current.Shards[rig.North.Id].Evidence.ShouldBeSameAs(other);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task EveryClassifiedFailureIsRecordedAsItsClassificationAlone(ShardCallFailure failure)
    {
        await using var rig = new RefreshRig();
        rig.Start();

        ShardCallException exception = failure == ShardCallFailure.Unclassified
            ? new ShardCallException("https://secret.example.invalid/fhir?token=hunter2")
            : new ShardCallException(rig.East, failure, "reason", 503);
        await rig.SweepAsync(exception, RefreshEvidence.Advertising("Patient"), RefreshEvidence.Advertising("Patient"));

        ShardCapabilityEntry east = rig.Refresher.Current.Shards[rig.East.Id];
        east.Failure.ShouldBe(failure);
        east.IsIncomplete.ShouldBeTrue();
    }

    [Fact]
    public async Task AFailureRetainsPriorEvidenceAsDegradedAndALaterSuccessReplacesItAndClearsTheFailure()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence first = RefreshEvidence.Advertising("Patient");
        ShardCapabilityEvidence westFirst = RefreshEvidence.Advertising("Organization");
        rig.Start();
        await rig.SweepAsync(first, westFirst, RefreshEvidence.Advertising("Location"));
        ShardCapabilitySnapshot afterFirstSweep = rig.Refresher.Current;

        // Second sweep: east fails, west is replaced by fresh evidence.
        rig.Time.Advance(Interval);
        ShardCapabilityEvidence westSecond = RefreshEvidence.Advertising("Observation");
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Transport, "wire"),
            westSecond,
            RefreshEvidence.Advertising("Location"));

        ShardCapabilityEntry degraded = rig.Refresher.Current.Shards[rig.East.Id];
        degraded.Evidence.ShouldBeSameAs(first);
        (degraded.Failure, degraded.IsIncomplete, degraded.IsDegraded).ShouldBe((ShardCallFailure.Transport, false, true));
        RefreshEvidence.ResourceTypes(rig.Refresher.Current.Shards[rig.West.Id].Evidence!).ShouldBe(["Observation"]);

        // The earlier snapshot is untouched by the later publications.
        afterFirstSweep.Shards[rig.East.Id].Failure.ShouldBeNull();
        afterFirstSweep.Shards[rig.West.Id].Evidence.ShouldBeSameAs(westFirst);

        // Third sweep: east recovers.
        rig.Time.Advance(Interval);
        ShardCapabilityEvidence recovered = RefreshEvidence.Advertising("Encounter");
        await rig.SweepAsync(recovered, RefreshEvidence.Advertising("Observation"), RefreshEvidence.Advertising("Location"));

        ShardCapabilityEntry healthy = rig.Refresher.Current.Shards[rig.East.Id];
        healthy.Evidence.ShouldBeSameAs(recovered);
        (healthy.Failure, healthy.IsIncomplete, healthy.IsDegraded).ShouldBe((null, false, false));
        (rig.Refresher.Current.IsComplete, rig.Refresher.Current.IsDegraded).ShouldBe((true, false));
    }

    [Fact]
    public async Task ARepeatedFailureKeepsTheOriginalEvidenceAndTheLatestClassification()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence first = RefreshEvidence.Advertising("Patient");
        rig.Start();
        await rig.SweepAsync(first, RefreshEvidence.Advertising("Patient"), RefreshEvidence.Advertising("Patient"));
        rig.Time.Advance(Interval);
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Timeout, "deadline"),
            RefreshEvidence.Advertising("Patient"),
            RefreshEvidence.Advertising("Patient"));

        rig.Time.Advance(Interval);
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Authentication, "refused", 401),
            RefreshEvidence.Advertising("Patient"),
            RefreshEvidence.Advertising("Patient"));

        ShardCapabilityEntry east = rig.Refresher.Current.Shards[rig.East.Id];
        east.Evidence.ShouldBeSameAs(first);
        east.Failure.ShouldBe(ShardCallFailure.Authentication);
        east.IsDegraded.ShouldBeTrue();
    }
}

using System.Collections.Immutable;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Http;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// How a <see cref="ShardCapabilitySnapshot"/> becomes planner input (design spec §9.1): one view for the whole registry,
/// from the snapshot's own advertisements and proof bound to those exact instances. Absence yields no view, never a partial one.
/// </summary>
public sealed class ShardCapabilitySnapshotProjectionTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static readonly SearchCertification NoProof = new(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);

    private static readonly SearchCertification ExactProof = new(
        new Dictionary<SearchParamType, IReadOnlyCollection<string>> { [SearchParamType.String] = ["exact"] }, [], [], [], []);

    [Fact]
    public async Task APendingOrPartialSnapshotHasNoViewEvenWithProofForEveryShard()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence east = Patient("name");
        ShardCapabilityEvidence west = Patient("name");
        ShardCapabilityEvidence north = Patient("name");
        ImmutableArray<ShardCapabilityProof> eager = Proofs(rig, east, west, north);

        Project(rig.Refresher.Current, eager).ShouldBeNull();

        rig.Start();
        await rig.SweepAsync(east, new ShardCallException(rig.West, ShardCallFailure.Timeout, "deadline"), north);
        rig.Refresher.Current.Shards[rig.West.Id].IsIncomplete.ShouldBeTrue();

        Project(rig.Refresher.Current, eager).ShouldBeNull();
    }

    [Theory]
    [InlineData("complete and matching")]
    [InlineData("a shard has no proof")]
    [InlineData("a descriptor of another registry with the same id")]
    [InlineData("proof for an equal but different advertisement instance")]
    public async Task ACompleteSnapshotNeedsProofForEveryShardsExactDescriptorAndEvidence(string scenario)
    {
        await using var rig = new RefreshRig();
        await using var other = new RefreshRig();
        ShardCapabilityEvidence[] evidence = [Patient("name"), Patient("name"), Patient("name")];
        rig.Start();
        await rig.SweepAsync(evidence[0], evidence[1], evidence[2]);
        ImmutableArray<ShardCapabilityProof> proofs = scenario switch
        {
            "complete and matching" => Proofs(rig, evidence[0], evidence[1], evidence[2]),
            "a shard has no proof" => [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1])],
            // The same evidence reference isolates descriptor identity: only the registry differs.
            "a descriptor of another registry with the same id" =>
                [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1]), Proof(other.North, evidence[2])],
            _ => [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1]), Proof(rig.North, Patient("name"))],
        };

        SearchCapabilities? view = Project(rig.Refresher.Current, proofs);

        (view is not null).ShouldBe(scenario == "complete and matching");
        other.North.Id.ShouldBe(rig.North.Id);
        other.Registry.Version.ShouldBe(rig.Registry.Version);
    }

    [Fact]
    public async Task TheViewIsTheRealIntersectionOfEveryAdvertisementAndCitesTheProducingRegistry()
    {
        await using var rig = new RefreshRig();
        await using var other = new RefreshRig();
        rig.Start();
        await rig.SweepAsync(Patient("name", "family"), Patient("name", "given"), Patient("name", "family", "given"));
        other.Start();
        ShardCapabilityEvidence[] otherEvidence = [Patient("name"), Patient("name"), Patient("name")];
        await other.SweepAsync(otherEvidence[0], otherEvidence[1], otherEvidence[2]);
        ShardCapabilitySnapshot snapshot = rig.Refresher.Current;
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);

        SearchCapabilities view = Project(snapshot, [proofs[2], proofs[1], proofs[0]]).ShouldNotBeNull();
        SearchCapabilities otherView = Project(other.Refresher.Current, ProofsFor(other, other.Refresher.Current)).ShouldNotBeNull();

        view.Registry.ShouldBeSameAs(rig.Registry);
        otherView.Registry.ShouldBeSameAs(other.Registry);
        view.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["name"]);
        var planner = new SearchPlanner(view, new ChainResolutionOptions(), new LimitOptions());
        Plan(planner, "name=Jones").Plan.ShouldNotBeNull();
        Plan(planner, "family=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
    }

    [Fact]
    public async Task AnExactModifierNeedsEveryOwnersSuppliedProofAndADeliberatelyEmptyProofIsNotAbsent()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence[] evidence = [Patient("name"), Patient("name"), Patient("name")];
        rig.Start();
        await rig.SweepAsync(evidence[0], evidence[1], evidence[2]);

        SearchCapabilities allProven = Project(rig.Refresher.Current, Proofs(rig, evidence[0], evidence[1], evidence[2], ExactProof)).ShouldNotBeNull();
        SearchCapabilities oneEmpty = Project(
            rig.Refresher.Current,
            [Proof(rig.East, evidence[0], ExactProof), Proof(rig.West, evidence[1], NoProof), Proof(rig.North, evidence[2], ExactProof)])
            .ShouldNotBeNull();

        Plan(Planner(allProven), "name:exact=Jones").Plan.ShouldNotBeNull();
        Plan(Planner(oneEmpty), "name:exact=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedModifier);
        Plan(Planner(oneEmpty), "name=Jones").Plan.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnAdvertisementThePlannerCannotUseSurfacesTheBridgeErrorInsteadOfBeingSkipped()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence unusable = CapabilityJson.Read(CapabilityJson.ServerStatement());
        ShardCapabilityEvidence usable = Patient("name");
        rig.Start();
        await rig.SweepAsync(usable, unusable, usable);

        Should.Throw<ArgumentException>(() => Project(rig.Refresher.Current, Proofs(rig, usable, unusable, usable)));
    }

    [Fact]
    public async Task DuplicateBindingsOfOneExactPairAreRefusedWhicheverComesFirst()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence[] evidence = [Patient("name"), Patient("name"), Patient("name")];
        rig.Start();
        await rig.SweepAsync(evidence[0], evidence[1], evidence[2]);
        ShardCapabilityProof first = Proof(rig.East, evidence[0], NoProof);
        ShardCapabilityProof second = Proof(rig.East, evidence[0], ExactProof);
        ShardCapabilityProof west = Proof(rig.West, evidence[1]);
        ShardCapabilityProof north = Proof(rig.North, evidence[2]);

        Should.Throw<ArgumentException>(() => Project(rig.Refresher.Current, [first, second, west, north]));
        Should.Throw<ArgumentException>(() => Project(rig.Refresher.Current, [second, first, west, north]));
        Should.Throw<ArgumentException>(() => Project(rig.Refresher.Current, default));
    }

    [Fact]
    public async Task OnlyTheRetainedInstanceKeepsItsProofThroughAFailureAndAChangedAdvertisementIsCheckedAnew()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence east = Patient("name", "family");
        ShardCapabilityEvidence west = Patient("name", "family");
        ShardCapabilityEvidence north = Patient("name", "family");
        rig.Start();
        await rig.SweepAsync(east, west, north);
        ShardCapabilitySnapshot first = rig.Refresher.Current;
        SearchCapabilities firstView = Project(first, Proofs(rig, east, west, north)).ShouldNotBeNull();

        // East and north fail transiently and keep their evidence; west answers again with a fresh, equal-looking instance.
        rig.Time.Advance(Interval);
        ShardCapabilityEvidence westAgain = Patient("name", "family");
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Transport, "wire"),
            westAgain,
            new ShardCallException(rig.North, ShardCallFailure.Timeout, "deadline"));
        ShardCapabilitySnapshot second = rig.Refresher.Current;

        second.IsDegraded.ShouldBeTrue();
        Project(second, Proofs(rig, east, west, north)).ShouldBeNull();
        Project(second, Proofs(rig, Patient("name", "family"), westAgain, north)).ShouldBeNull();
        Project(second, Proofs(rig, east, westAgain, north)).ShouldNotBeNull();

        // A later, different advertisement replaces the one the previous proof was for.
        rig.Time.Advance(Interval);
        ShardCapabilityEvidence westChanged = Patient("name");
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Transport, "wire"),
            westChanged,
            new ShardCallException(rig.North, ShardCallFailure.Timeout, "deadline"));
        ShardCapabilitySnapshot third = rig.Refresher.Current;

        Project(third, Proofs(rig, east, westAgain, north)).ShouldBeNull();
        SearchCapabilities thirdView = Project(third, Proofs(rig, east, westChanged, north)).ShouldNotBeNull();

        // Nothing captured earlier moved: neither the snapshots, nor the views taken from them.
        Project(first, Proofs(rig, east, west, north)).ShouldNotBeNull();
        Project(second, Proofs(rig, east, westAgain, north)).ShouldNotBeNull();
        Plan(Planner(firstView), "family=Jones").Plan.ShouldNotBeNull();
        Plan(Planner(thirdView), "family=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
        firstView.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["family", "name"], ignoreOrder: true);
    }

    [Fact]
    public async Task ProjectionNeitherFetchesNorWaitsOnAPausedFetchOrAStoppedRefresher()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence[] evidence = [Patient("name"), Patient("name"), Patient("name")];
        rig.Start();
        await rig.SweepAsync(evidence[0], evidence[1], evidence[2]);
        rig.Time.Advance(Interval);
        PendingFetch paused = await rig.Script.NextAsync();
        paused.Shard.ShouldBeSameAs(rig.East);
        ImmutableArray<ShardCapabilityProof> proofs = Proofs(rig, evidence[0], evidence[1], evidence[2]);

        Project(rig.Refresher.Current, proofs).ShouldNotBeNull();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse();

        rig.Stop();
        await rig.EndedAsync();

        Project(rig.Refresher.Current, proofs).ShouldNotBeNull();
        rig.Script.TryTakeStarted(out _).ShouldBeFalse();
    }

    private static ShardCapabilityEvidence Patient(params string[] searchCodes) =>
        CapabilityJson.Read(CapabilityJson.ServerStatement(CapabilityJson.Arr(CapabilityJson.SearchableResource(
            "Patient",
            ",\"searchParam\":" + CapabilityJson.Arr([.. searchCodes.Select(code => CapabilityJson.Param(code, "string"))])))));

    private static ShardCapabilityProof Proof(ShardDescriptor shard, ShardCapabilityEvidence evidence, SearchCertification? certification = null) =>
        new(shard, evidence, certification ?? NoProof);

    private static ImmutableArray<ShardCapabilityProof> Proofs(
        RefreshRig rig, ShardCapabilityEvidence east, ShardCapabilityEvidence west, ShardCapabilityEvidence north, SearchCertification? certification = null) =>
        [Proof(rig.East, east, certification), Proof(rig.West, west, certification), Proof(rig.North, north, certification)];

    private static ImmutableArray<ShardCapabilityProof> ProofsFor(RefreshRig rig, ShardCapabilitySnapshot snapshot) =>
        [.. rig.Registry.Shards.Select(shard => Proof(shard, snapshot.Shards[shard.Id].Evidence!))];

    private static SearchCapabilities? Project(ShardCapabilitySnapshot snapshot, ImmutableArray<ShardCapabilityProof> proofs)
    {
        bool created = snapshot.TryCreateSearchCapabilities(proofs, out SearchCapabilities? view);
        (view is not null).ShouldBe(created);
        return view;
    }

    private static SearchPlanner Planner(SearchCapabilities view) => new(view, new ChainResolutionOptions(), new LimitOptions());

    private static SearchPlanResult Plan(SearchPlanner planner, string query) =>
        planner.Plan(ResourceType.Parse("Patient"), SearchQuery.Parse(query).Query.ShouldNotBeNull());
}

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
/// The narrow Patient projection of a <see cref="ShardCapabilitySnapshot"/>: the caller names the ordinary mandatory Patient
/// search codes and receives one view that publishes only those, with no includes and none of the optional proofs, or no view
/// at all. The broad projection is the control for what the narrow one must not leak.
/// </summary>
public sealed class ShardCapabilitySnapshotPatientProjectionTests
{
    private const string R4IdCanonical = "http://hl7.org/fhir/SearchParameter/Resource-id";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static readonly ResourceType PatientType = ResourceType.Parse("Patient");

    private static readonly SearchCertification NoProof = new(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);

    private static readonly SearchCertification BroadProof = new(
        new Dictionary<SearchParamType, IReadOnlyCollection<string>> { [SearchParamType.String] = ["exact"] },
        [SearchParamType.String, SearchParamType.Token],
        [SearchParamType.String, SearchParamType.Token],
        ["Patient?"],
        [new ReferencePathCertification(
            new ReferencePath(PatientType, "organization", ResourceType.Parse("Organization")),
            ReferencePathOperations.IncludeExtraction,
            ReferenceSearchForms.None,
            ReferenceSearchForms.None)]);

    [Theory]
    [InlineData("common only")]
    [InlineData("per type only")]
    [InlineData("split")]
    public async Task IdAndGivenArePublishedFromWhicheverDeclarationsCarryThemAndNothingElseLeaks(string layout)
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence evidence = layout switch
        {
            "common only" => Advertise([Id, Given, Family], [Name]),
            "per type only" => Advertise([], [Id, Given, Family, Name]),
            _ => Advertise([Id], [Given, Family, Name]),
        };
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence, evidence, evidence);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);

        SearchCapabilities view = Narrow(snapshot, proofs, "_id", "given").ShouldNotBeNull();
        SearchCapabilities broad = Broad(snapshot, proofs);

        view.Registry.ShouldBeSameAs(rig.Registry);
        view.Resources.Select(r => r.Type.Value).ShouldBe(["Patient"]);
        view.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id", "given"]);
        broad.Resources.Select(r => r.Type.Value).ShouldBe(["Observation", "Organization", "Patient"]);
        broad.Resources.Single(r => r.Type == PatientType).Parameters.Select(p => p.Code).ShouldBe(["_id", "family", "given", "name"]);
        SearchPlanner planner = Planner(view);
        Plan(planner, "_id=1&given=Jones").Plan.ShouldNotBeNull();
        Plan(planner, "family=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
        Plan(Planner(broad), "family=Jones").Plan.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnIdOnlySelectorPublishesJustIdEvenWhenOtherCodesAreDeclaredEverywhere()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence evidence = Advertise([Id], [Given, Name]);
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence, evidence, evidence);

        SearchCapabilities view = Narrow(snapshot, ProofsFor(rig, snapshot), "_id").ShouldNotBeNull();

        view.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id"]);
        Plan(Planner(view), "_id=1").Plan.ShouldNotBeNull();
        Plan(Planner(view), "given=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedParameter);
    }

    [Fact]
    public async Task NarrowProjectionWithholdsInclusionsAndOptionalSearchProofs()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence evidence = Advertise([Id], [Given], includes: true);
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence, evidence, evidence);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot, BroadProof);

        SearchCapabilities broad = Broad(snapshot, proofs);
        SearchCapabilities view = Narrow(snapshot, proofs, "_id", "given").ShouldNotBeNull();

        // Control: the same evidence and proofs, projected broadly, do carry them.
        broad.Resources.Single(r => r.Type == PatientType).SearchIncludes.ShouldBe(["Patient:organization"]);
        Plan(Planner(broad), "given:exact=Jones").Plan.ShouldNotBeNull();
        Plan(Planner(broad), "given=a&given=b").Plan.ShouldNotBeNull();
        Plan(Planner(broad), "_id=1,2").Plan.ShouldNotBeNull();
        Plan(Planner(broad), "_id=1&_include=Patient:organization").Plan.ShouldNotBeNull();
        Plan(Planner(broad), "_count=0&_total=accurate").Plan.ShouldNotBeNull();

        ResourceSearchSupport patient = view.Resources.Single();
        patient.SearchIncludes.ShouldBeEmpty();
        patient.SearchRevIncludes.ShouldBeEmpty();
        Plan(Planner(view), "given:exact=Jones").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedModifier);
        Plan(Planner(view), "given=a&given=b").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedRepetition);
        Plan(Planner(view), "_id=1,2").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.UnsupportedAlternatives);
        Plan(Planner(view), "_id=1&_include=Patient:organization").Failure.ShouldNotBeNull();
        Plan(Planner(view), "_count=0&_total=accurate").Failure.ShouldNotBeNull().Code.ShouldBe(SearchFailureCode.ExactCountNotCertified);
        Plan(Planner(view), "_id=1&given=Jones").Plan.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Patient is not searchable on one shard")]
    [InlineData("a required code is missing on one shard")]
    [InlineData("a required code is declared nowhere")]
    public async Task AMandatoryResourceOrCodeMissingOnAnyShardYieldsNoViewWhereTheBroadViewSilentlyNarrows(string scenario)
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence full = Advertise([Id], [Given, Name]);
        ShardCapabilityEvidence north = scenario switch
        {
            "Patient is not searchable on one shard" => Advertise([Id], [], withPatient: false),
            "a required code is missing on one shard" => Advertise([Id], [Name]),
            _ => full,
        };
        ShardCapabilityEvidence east = scenario == "a required code is declared nowhere" ? Advertise([Id], [Name]) : full;
        ShardCapabilityEvidence west = scenario == "a required code is declared nowhere" ? Advertise([Id], [Name]) : full;
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, east, west, north);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);

        Narrow(snapshot, proofs, "_id", "given").ShouldBeNull();

        // A selector the shards all do declare is unaffected, unless Patient itself is missing.
        (Narrow(snapshot, proofs, "_id") is not null).ShouldBe(scenario != "Patient is not searchable on one shard");
        Broad(snapshot, proofs).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("given", true)]
    [InlineData("given", false)]
    [InlineData("_id", true)]
    [InlineData("_id", false)]
    public async Task ADisagreementCoreRefusesYieldsNoViewEvenWhenTheOtherDeclarationAgrees(string conflicted, bool onCommon)
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence clean = Advertise([Id], [Given]);
        string wrongType = CapabilityJson.Param("given", "token");
        string wrongCanonical = CapabilityJson.Param("_id", "token", "http://example.org/SearchParameter/other-id");
        string conflict = conflicted == "given" ? wrongType : wrongCanonical;

        // The conflicting declaration sits on one shard, against an agreeing one on the other list of that same shard.
        ShardCapabilityEvidence west = (conflicted, onCommon) switch
        {
            ("given", true) => Advertise([Id, wrongType], [Given]),
            ("given", false) => Advertise([Id, Given], [wrongType]),
            (_, true) => Advertise([conflict], [Id, Given]),
            _ => Advertise([Id], [conflict, Given]),
        };
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, clean, west, clean);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);
        string other = conflicted == "given" ? "_id" : "given";

        Narrow(snapshot, proofs, "_id", "given").ShouldBeNull();
        Narrow(snapshot, proofs, conflicted).ShouldBeNull();
        SearchCapabilities partial = Narrow(snapshot, proofs, other).ShouldNotBeNull();
        partial.Resources.Single().Parameters.Select(p => p.Code).ShouldBe([other]);
    }

    [Fact]
    public async Task AgreeingDuplicatesAcrossCommonAndPerTypeDeclarationsArePublished()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence agreeing = Advertise([CapabilityJson.Param("_id", "token", R4IdCanonical)], [Id]);
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, agreeing, agreeing, agreeing);

        SearchCapabilities view = Narrow(snapshot, ProofsFor(rig, snapshot), "_id").ShouldNotBeNull();

        view.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id"]);
    }

    [Fact]
    public async Task CodesAreMatchedOrdinallyWithoutCaseFolding()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence evidence = Advertise([Id], [Given]);
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence, evidence, evidence);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);

        Narrow(snapshot, proofs, "_ID").ShouldBeNull();
        Narrow(snapshot, proofs, "Given").ShouldBeNull();
        Narrow(snapshot, proofs, "_id", "not a code").ShouldBeNull();
        Narrow(snapshot, proofs, "_id", "_id").ShouldNotBeNull();
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
        ShardCapabilityEvidence[] evidence = [Advertise([Id], [Given]), Advertise([Id], [Given]), Advertise([Id], [Given])];
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence[0], evidence[1], evidence[2]);
        ImmutableArray<ShardCapabilityProof> proofs = scenario switch
        {
            "complete and matching" => ProofsFor(rig, snapshot),
            "a shard has no proof" => [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1])],
            "a descriptor of another registry with the same id" =>
                [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1]), Proof(other.North, evidence[2])],
            _ => [Proof(rig.East, evidence[0]), Proof(rig.West, evidence[1]), Proof(rig.North, Advertise([Id], [Given]))],
        };

        (Narrow(snapshot, proofs, "_id", "given") is not null).ShouldBe(scenario == "complete and matching");
    }

    [Fact]
    public async Task APendingOrPartialSnapshotHasNoViewEvenWithProofForEveryShard()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence east = Advertise([Id], [Given]);
        ShardCapabilityEvidence west = Advertise([Id], [Given]);
        ShardCapabilityEvidence north = Advertise([Id], [Given]);
        ImmutableArray<ShardCapabilityProof> eager = [Proof(rig.East, east), Proof(rig.West, west), Proof(rig.North, north)];

        Narrow(rig.Refresher.Current, eager, "_id").ShouldBeNull();

        rig.Start();
        await rig.SweepAsync(east, new ShardCallException(rig.West, ShardCallFailure.Timeout, "deadline"), north);
        rig.Refresher.Current.Shards[rig.West.Id].IsIncomplete.ShouldBeTrue();

        Narrow(rig.Refresher.Current, eager, "_id").ShouldBeNull();
    }

    [Fact]
    public async Task OnlyTheRetainedInstanceKeepsItsProofAndANewSuccessfulFetchNeedsANewOneWhileOldSnapshotsStayValid()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence east = Advertise([Id], [Given]);
        ShardCapabilityEvidence west = Advertise([Id], [Given]);
        ShardCapabilityEvidence north = Advertise([Id], [Given]);
        ShardCapabilitySnapshot first = await CompleteAsync(rig, east, west, north);
        ImmutableArray<ShardCapabilityProof> firstProofs = [Proof(rig.East, east), Proof(rig.West, west), Proof(rig.North, north)];
        SearchCapabilities firstView = Narrow(first, firstProofs, "_id", "given").ShouldNotBeNull();

        // East and north fail and keep their evidence, so identical evidence still matches; west answers with a fresh, equal-looking instance.
        rig.Time.Advance(Interval);
        ShardCapabilityEvidence westAgain = Advertise([Id], [Given]);
        await rig.SweepAsync(
            new ShardCallException(rig.East, ShardCallFailure.Transport, "wire"),
            westAgain,
            new ShardCallException(rig.North, ShardCallFailure.Timeout, "deadline"));
        ShardCapabilitySnapshot second = rig.Refresher.Current;

        second.IsDegraded.ShouldBeTrue();
        Narrow(second, firstProofs, "_id", "given").ShouldBeNull();
        Narrow(second, [Proof(rig.East, east), Proof(rig.West, westAgain), Proof(rig.North, north)], "_id", "given").ShouldNotBeNull();

        // Nothing captured earlier moved.
        Narrow(first, firstProofs, "_id", "given").ShouldNotBeNull();
        firstView.Resources.Single().Parameters.Select(p => p.Code).ShouldBe(["_id", "given"]);
    }

    [Fact]
    public async Task CallerArgumentErrorsAreRefusedRatherThanProjectedBroadly()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence[] evidence = [Advertise([Id], [Given]), Advertise([Id], [Given]), Advertise([Id], [Given])];
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, evidence[0], evidence[1], evidence[2]);
        ImmutableArray<ShardCapabilityProof> proofs = ProofsFor(rig, snapshot);
        ImmutableArray<ShardCapabilityProof> twice = [Proof(rig.East, evidence[0]), Proof(rig.East, evidence[0], BroadProof), proofs[1], proofs[2]];

        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(default, ["_id"], out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities([null!, .. proofs], ["_id"], out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(twice, ["_id"], out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(proofs, default, out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(proofs, [], out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(proofs, ["_id", ""], out _));
        Should.Throw<ArgumentException>(() => snapshot.TryCreatePatientSearchCapabilities(proofs, ["_id", null!], out _));
    }

    [Fact]
    public async Task AnEmptyAdvertisementYieldsNoNarrowViewWithoutChangingTheBroadError()
    {
        await using var rig = new RefreshRig();
        ShardCapabilityEvidence usable = Advertise([Id], [Given]);
        ShardCapabilityEvidence unusable = CapabilityJson.Read(CapabilityJson.ServerStatement());
        ShardCapabilitySnapshot snapshot = await CompleteAsync(rig, usable, unusable, usable);

        // No Patient on a shard is absence, not an unusable advertisement: it yields no view rather than throwing.
        Narrow(snapshot, [Proof(rig.East, usable), Proof(rig.West, unusable), Proof(rig.North, usable)], "_id").ShouldBeNull();
        Should.Throw<ArgumentException>(() => Broad(snapshot, [Proof(rig.East, usable), Proof(rig.West, unusable), Proof(rig.North, usable)]));
    }

    private static string Id => CapabilityJson.Param("_id", "token");

    private static string Given => CapabilityJson.Param("given", "string");

    private static string Family => CapabilityJson.Param("family", "string");

    private static string Name => CapabilityJson.Param("name", "string");

    private static ShardCapabilityEvidence Advertise(string[] common, string[] patient, bool includes = false, bool withPatient = true)
    {
        string patientMembers = ",\"searchParam\":" + CapabilityJson.Arr(patient)
            + (includes ? ",\"searchInclude\":[\"Patient:organization\"],\"searchRevInclude\":[\"Observation:subject\"]" : string.Empty);
        string[] resources =
        [
            .. withPatient ? [CapabilityJson.SearchableResource("Patient", patientMembers)] : Array.Empty<string>(),
            CapabilityJson.SearchableResource("Observation", ",\"searchParam\":" + CapabilityJson.Arr(Id)),
            CapabilityJson.SearchableResource("Organization", ",\"searchParam\":" + CapabilityJson.Arr(Id)),
        ];
        return CapabilityJson.Read(CapabilityJson.ServerStatement(CapabilityJson.Arr(resources), CapabilityJson.Arr(common)));
    }

    private static async Task<ShardCapabilitySnapshot> CompleteAsync(RefreshRig rig, params ShardCapabilityEvidence[] evidence)
    {
        rig.Start();
        await rig.SweepAsync([.. evidence.Cast<object>()]);
        return rig.Refresher.Current;
    }

    private static ShardCapabilityProof Proof(ShardDescriptor shard, ShardCapabilityEvidence evidence, SearchCertification? certification = null) =>
        new(shard, evidence, certification ?? NoProof);

    private static ImmutableArray<ShardCapabilityProof> ProofsFor(RefreshRig rig, ShardCapabilitySnapshot snapshot, SearchCertification? certification = null) =>
        [.. rig.Registry.Shards.Select(shard => Proof(shard, snapshot.Shards[shard.Id].Evidence!, certification))];

    private static SearchCapabilities Broad(ShardCapabilitySnapshot snapshot, ImmutableArray<ShardCapabilityProof> proofs)
    {
        snapshot.TryCreateSearchCapabilities(proofs, out SearchCapabilities? view).ShouldBeTrue();
        return view.ShouldNotBeNull();
    }

    private static SearchCapabilities? Narrow(ShardCapabilitySnapshot snapshot, ImmutableArray<ShardCapabilityProof> proofs, params string[] codes)
    {
        bool created = snapshot.TryCreatePatientSearchCapabilities(proofs, [.. codes], out SearchCapabilities? view);
        (view is not null).ShouldBe(created);
        return view;
    }

    private static SearchPlanner Planner(SearchCapabilities view) => new(view, new ChainResolutionOptions(), new LimitOptions());

    private static SearchPlanResult Plan(SearchPlanner planner, string query) =>
        planner.Plan(PatientType, SearchQuery.Parse(query).Query.ShouldNotBeNull());
}

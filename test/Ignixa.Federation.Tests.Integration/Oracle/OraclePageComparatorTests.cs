using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Ignixa.Federation.Tests.Integration.Oracle;

/// <summary>
/// Drives the comparator with hand-authored searchset pages against the real committed manifest.
/// The registered bases differ from the manifest's reserved bases, so every collision answer is rebased.
/// </summary>
public sealed class OraclePageComparatorTests
{
    private const string East = "http://127.0.0.1:5101/fhir/";
    private const string West = "http://127.0.0.1:5102/";
    private const string OracleUrl = "http://127.0.0.1:5100/";
    private const string Unregistered = "http://127.0.0.1:5199/";

    // East and the oracle are registered without a trailing slash to exercise base normalization.
    private static readonly OracleBases Bases = new(
        new Dictionary<string, string> { ["east"] = "http://127.0.0.1:5101/fhir", ["west"] = West },
        "http://127.0.0.1:5100");

    [Fact]
    public void IncludeForward_CrossShard_SharedIncludeRepeatedOnEachPage_AndOraclePagedInAnotherOrder_Passes()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);

        OraclePageComparator.Compare(c, CrossShardIncludeForwardFederation(), IncludeForwardOracle()).ShouldBeEmpty();
    }

    [Fact]
    public void IncludeForward_SharedIncludeOmittedOnSecondPageThatNeedsIt_Fails()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(West, "Observation", "obs-cedar-1"), Include(West, "Patient", "p-cedar")),
            Page(next: true, Match(East, "Observation", "obs-orchid-1"), Include(East, "Patient", "p-orchid")),
            Page(next: false, Match(West, "Observation", "obs-orchid-2")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, IncludeForwardOracle());

        ShouldReport(failures, "[include.forward] federation page 3: missing include Patient/p-orchid", "Observation/obs-orchid-2");
    }

    [Fact]
    public void IncludeForward_CrossShard_IncludeReturnedUnderShardThatDoesNotOwnIt_Fails()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(West, "Observation", "obs-cedar-1"), Include(West, "Patient", "p-cedar")),
            Page(next: true, Match(East, "Observation", "obs-orchid-1"), Include(East, "Patient", "p-orchid")),
            Page(next: false, Match(West, "Observation", "obs-orchid-2"), Include(West, "Patient", "p-orchid")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, IncludeForwardOracle());

        ShouldReport(
            failures,
            "[include.forward] federation page 3: http://127.0.0.1:5102/Patient/p-orchid: Patient/p-orchid is not seeded on shard 'west' (seeded on: east)");
    }

    [Fact]
    public void IncludeForward_CrossShardPagesJudgedAgainstColocatedPlacement_FailsOwnershipOfMovedObservation()
    {
        var c = OracleFixtures.Bind("include.forward", "colocated", Bases);

        var failures = OraclePageComparator.Compare(c, CrossShardIncludeForwardFederation(), IncludeForwardOracle());

        failures.ShouldHaveSingleItem().ShouldBe(
            "[include.forward] federation page 3: http://127.0.0.1:5102/Observation/obs-orchid-2: Observation/obs-orchid-2 is not seeded on shard 'west' (seeded on: east)");
    }

    [Fact]
    public void FullUrlNotAlignedWithBody_Fails()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(West, "Observation", "obs-cedar-1"), Include(West, "Patient", "p-cedar")),
            Page(next: true, Entry("match", East + "Observation/obs-orchid-1", "Observation", "obs-cedar-1"), Include(East, "Patient", "p-orchid")),
            Page(next: false, Match(West, "Observation", "obs-orchid-2"), Include(East, "Patient", "p-orchid")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, IncludeForwardOracle());

        ShouldReport(
            failures,
            "[include.forward] federation page 2: fullUrl http://127.0.0.1:5101/fhir/Observation/obs-orchid-1 does not match body Observation/obs-cedar-1");
    }

    [Fact]
    public void PageWithMoreMatchesThanCount_Fails()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(
                next: true,
                Match(West, "Observation", "obs-cedar-1"),
                Match(East, "Observation", "obs-orchid-1"),
                Include(West, "Patient", "p-cedar"),
                Include(East, "Patient", "p-orchid")),
            Page(next: false, Match(West, "Observation", "obs-orchid-2"), Include(East, "Patient", "p-orchid")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, IncludeForwardOracle());

        failures.ShouldHaveSingleItem().ShouldBe("[include.forward] federation page 1: 2 match entries exceed count 1");
    }

    [Fact]
    public void MatchRepeatedOnALaterPage_Fails()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(East, "Observation", "obs-orchid-1"), Include(East, "Patient", "p-orchid")),
            Page(next: true, Match(West, "Observation", "obs-cedar-1"), Include(West, "Patient", "p-cedar")),
            Page(next: false, Match(East, "Observation", "obs-orchid-1"), Include(East, "Patient", "p-orchid")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, IncludeForwardOracle());

        ShouldReport(failures, "[include.forward] federation: match Observation/obs-orchid-1 repeated on page 1 and page 3");
        ShouldReport(failures, "[include.forward] federation: missing match Observation/obs-orchid-2");
    }

    [Fact]
    public void PassthroughControl_FederationMissesRelocatedObservation_OracleReturnsIt_Passes()
    {
        var c = OracleFixtures.Bind("chain.cross-shard.passthrough-control", "cross-shard", Bases);
        JsonNode[] federation = [Page(next: false, Match(East, "Observation", "obs-orchid-1"))];
        JsonNode[] oracle =
        [
            Page(next: false, Match(OracleUrl, "Observation", "obs-orchid-2"), Match(OracleUrl, "Observation", "obs-orchid-1")),
        ];

        OraclePageComparator.Compare(c, federation, oracle).ShouldBeEmpty();
    }

    [Fact]
    public void PassthroughControl_FederationReturnsRelocatedObservation_ReportsControlFailure()
    {
        var c = OracleFixtures.Bind("chain.cross-shard.passthrough-control", "cross-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: false, Match(East, "Observation", "obs-orchid-1"), Match(West, "Observation", "obs-orchid-2")),
        ];
        JsonNode[] oracle =
        [
            Page(next: false, Match(OracleUrl, "Observation", "obs-orchid-1"), Match(OracleUrl, "Observation", "obs-orchid-2")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oracle);

        ShouldReport(
            failures,
            "[chain.cross-shard.passthrough-control] federation: Passthrough negative control failed: returned Observation/obs-orchid-2");
    }

    [Fact]
    public void PassthroughControl_OracleMissesRelocatedObservation_ReportsNonDiscriminatingControl()
    {
        var c = OracleFixtures.Bind("chain.cross-shard.passthrough-control", "cross-shard", Bases);
        JsonNode[] federation = [Page(next: false, Match(East, "Observation", "obs-orchid-1"))];
        JsonNode[] oracle = [Page(next: false, Match(OracleUrl, "Observation", "obs-orchid-1"))];

        var failures = OraclePageComparator.Compare(c, federation, oracle);

        ShouldReport(
            failures,
            "[chain.cross-shard.passthrough-control] oracle: negative control not discriminating: oracle must return Observation/obs-orchid-2");
    }

    [Fact]
    public void Oracle_MatchReturnedUnderShardBase_FailsOwnershipAgainstOracleBase()
    {
        // East seeds obs-orchid-1, so this entry passes only if the oracle is wrongly judged against shard bases.
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] oracle =
        [
            Page(next: true, Match(OracleUrl, "Observation", "obs-orchid-2"), Include(OracleUrl, "Patient", "p-orchid")),
            Page(next: true, Match(OracleUrl, "Observation", "obs-cedar-1"), Include(OracleUrl, "Patient", "p-cedar")),
            Page(next: false, Match(East, "Observation", "obs-orchid-1"), Include(OracleUrl, "Patient", "p-orchid")),
        ];

        var failures = OraclePageComparator.Compare(c, CrossShardIncludeForwardFederation(), oracle);

        failures.ShouldNotContain(f => f.StartsWith("[include.forward] federation", StringComparison.Ordinal));
        failures.ShouldHaveSingleItem().ShouldBe(
            "[include.forward] oracle page 3: fullUrl http://127.0.0.1:5101/fhir/Observation/obs-orchid-1 is not <oracle base>Type/id");
    }

    [Fact]
    public void Oracle_IncludeOmittedOnPageWhoseMatchNeedsIt_FailsIncludeCheck()
    {
        var c = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        JsonNode[] oracle =
        [
            Page(next: true, Match(OracleUrl, "Observation", "obs-orchid-2"), Include(OracleUrl, "Patient", "p-orchid")),
            Page(next: true, Match(OracleUrl, "Observation", "obs-cedar-1"), Include(OracleUrl, "Patient", "p-cedar")),
            Page(next: false, Match(OracleUrl, "Observation", "obs-orchid-1")),
        ];

        var failures = OraclePageComparator.Compare(c, CrossShardIncludeForwardFederation(), oracle);

        failures.ShouldNotContain(f => f.StartsWith("[include.forward] federation", StringComparison.Ordinal));
        failures.ShouldHaveSingleItem().ShouldBe(
            "[include.forward] oracle page 3: missing include Patient/p-orchid (required by Observation/obs-orchid-1)");
    }

    [Fact]
    public void CollisionIncludeUnpaged_AllWitnessesWithBothPatientDupIncludes_Passes()
    {
        var c = OracleFixtures.Bind("collision.include.unpaged", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(
                next: false,
                Match(West, "Observation", "obs-dup-xref"),
                Match(East, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup"),
                Include(West, "Patient", "dup", "Gus"),
                Include(East, "Patient", "dup", "Eli")),
        ];

        OraclePageComparator.Compare(c, federation, oraclePages: null).ShouldBeEmpty();
    }

    [Fact]
    public void CollisionIncludeUnpaged_PatientDupIncludesDedupedByTypeId_FailsNamingMissingFullUrl()
    {
        var c = OracleFixtures.Bind("collision.include.unpaged", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(
                next: false,
                Match(East, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup-xref"),
                Include(East, "Patient", "dup", "Eli")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        ShouldReport(failures, "[collision.include.unpaged] federation page 1: missing include http://127.0.0.1:5102/Patient/dup");
    }

    [Fact]
    public void CollisionIncludeUnpaged_WitnessesSplitOverTwoPages_FailsAsNonDiscriminating()
    {
        var c = OracleFixtures.Bind("collision.include.unpaged", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(East, "Observation", "obs-dup"), Include(East, "Patient", "dup", "Eli")),
            Page(
                next: false,
                Match(West, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup-xref"),
                Include(West, "Patient", "dup", "Gus"),
                Include(East, "Patient", "dup", "Eli")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldHaveSingleItem().ShouldStartWith(
            "[collision.include.unpaged] federation: co-emitted witnesses split across pages 1, 2");
    }

    [Fact]
    public void CollisionIncludeUnpaged_WestPatientDupCarryingEastGivenName_FailsBodyCheck()
    {
        var c = OracleFixtures.Bind("collision.include.unpaged", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(
                next: false,
                Match(East, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup"),
                Match(West, "Observation", "obs-dup-xref"),
                Include(East, "Patient", "dup", "Eli"),
                Include(West, "Patient", "dup", "Eli")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldHaveSingleItem().ShouldBe(
            "[collision.include.unpaged] federation page 1: http://127.0.0.1:5102/Patient/dup: name[0].given is [Eli], expected [Gus]");
    }

    [Fact]
    public void CollisionTaggedUnion_OnePatientDupPerPage_Passes()
    {
        var c = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(West, "Patient", "dup", "Gus")),
            Page(next: false, Match(East, "Patient", "dup", "Eli")),
        ];

        OraclePageComparator.Compare(c, federation, oraclePages: null).ShouldBeEmpty();
    }

    [Fact]
    public void CollisionTaggedUnion_PatientDupOnUnregisteredBase_Fails()
    {
        var c = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(East, "Patient", "dup", "Eli")),
            Page(next: false, Match(Unregistered, "Patient", "dup", "Gus")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        ShouldReport(
            failures,
            "[collision.tagged-union] federation page 2: fullUrl http://127.0.0.1:5199/Patient/dup is not <registered shard base>Type/id");
        ShouldReport(
            failures,
            "[collision.tagged-union] federation page 2: Patient/dup at unlisted fullUrl http://127.0.0.1:5199/Patient/dup");
        ShouldReport(failures, "[collision.tagged-union] federation: missing match http://127.0.0.1:5102/Patient/dup");
    }

    [Fact]
    public void CollisionChainWest_BackendOvermatchOfCrossShardReference_FailsAsUnexpectedMatch()
    {
        var c = OracleFixtures.Bind("collision.chain.distributed.west", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: false, Match(West, "Observation", "obs-dup"), Match(West, "Observation", "obs-dup-xref")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldHaveSingleItem().ShouldStartWith(
            "[collision.chain.distributed.west] federation: unexpected match http://127.0.0.1:5102/Observation/obs-dup-xref");
    }

    [Fact]
    public void CollisionRevincludePaged_DuplicateIncludeWithinPage_Fails()
    {
        var c = OracleFixtures.Bind("collision.revinclude.paged", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(
                next: true,
                Match(East, "Patient", "dup", "Eli"),
                Include(East, "Observation", "obs-dup"),
                Include(West, "Observation", "obs-dup-xref"),
                Include(East, "Observation", "obs-dup")),
            Page(next: false, Match(West, "Patient", "dup", "Gus"), Include(West, "Observation", "obs-dup")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldHaveSingleItem().ShouldBe(
            "[collision.revinclude.paged] federation page 1: duplicate include http://127.0.0.1:5101/fhir/Observation/obs-dup");
    }

    [Fact]
    public void OraclePages_RequiredForUniqueIdCases_AndRejectedForCollisionCases()
    {
        var uniqueId = OracleFixtures.Bind("include.forward", "cross-shard", Bases);
        var collision = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] collisionPages =
        [
            Page(next: true, Match(East, "Patient", "dup", "Eli")),
            Page(next: false, Match(West, "Patient", "dup", "Gus")),
        ];

        ShouldReport(
            OraclePageComparator.Compare(uniqueId, CrossShardIncludeForwardFederation(), oraclePages: null),
            "[include.forward] oracle: no pages supplied");
        ShouldReport(
            OraclePageComparator.Compare(uniqueId, CrossShardIncludeForwardFederation(), oraclePages: []),
            "[include.forward] oracle: no pages supplied");
        ShouldReport(
            OraclePageComparator.Compare(collision, collisionPages, [Page(next: false, Match(OracleUrl, "Patient", "dup", "Eli"))]),
            "[collision.tagged-union] oracle: oracle is not used for collision cases");
    }

    [Fact]
    public void PageListTruncatedOrOverFollowed_Fails()
    {
        var c = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] truncated =
        [
            Page(next: true, Match(East, "Patient", "dup", "Eli")),
            Page(next: true, Match(West, "Patient", "dup", "Gus")),
        ];
        JsonNode[] missingNext =
        [
            Page(next: false, Match(East, "Patient", "dup", "Eli")),
            Page(next: false, Match(West, "Patient", "dup", "Gus")),
        ];

        OraclePageComparator.Compare(c, truncated, oraclePages: null).ShouldHaveSingleItem().ShouldStartWith(
            "[collision.tagged-union] federation page 2: last supplied page has a next link");
        OraclePageComparator.Compare(c, missingNext, oraclePages: null).ShouldHaveSingleItem().ShouldStartWith(
            "[collision.tagged-union] federation page 1: no next link, but further pages were supplied");
    }

    [Fact]
    public void Bind_RejectsMisconfiguredRuns()
    {
        var westOnly = new OracleBases(new Dictionary<string, string> { ["west"] = West }, OracleUrl);
        var duplicate = new OracleBases(new Dictionary<string, string> { ["east"] = East, ["west"] = East.TrimEnd('/') }, OracleUrl);
        var noOracle = new OracleBases(new Dictionary<string, string> { ["east"] = East, ["west"] = West }, oracle: null);

        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("no.such.case", "colocated", Bases)).Message.ShouldContain("no.such.case");
        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("chain.colocated.passthrough", "cross-shard", Bases)).Message.ShouldContain("cross-shard");
        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("collision.tagged-union", "per-shard", westOnly)).Message.ShouldContain("east");
        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("collision.tagged-union", "per-shard", duplicate)).Message.ShouldContain(East);
        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("include.forward", "colocated", noOracle)).Message.ShouldContain("oracle");
        Should.NotThrow(() => OracleFixtures.Bind("collision.tagged-union", "per-shard", noOracle));
    }

    [Fact]
    public void Bind_RejectsSpellingEquivalentBases()
    {
        var eastTwice = new Dictionary<string, string> { ["east"] = "https://east.example/fhir/", ["west"] = "https://EAST.example:443/fhir/" };
        var oracleIsEast = new Dictionary<string, string> { ["east"] = East, ["west"] = West };

        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("collision.tagged-union", "per-shard", new OracleBases(eastTwice, OracleUrl)))
            .Message.ShouldContain("https://east.example/fhir/ (east, west)");
        Should.Throw<ArgumentException>(() => OracleFixtures.Bind("include.forward", "colocated", new OracleBases(oracleIsEast, "HTTP://127.0.0.1:5101/fhir")))
            .Message.ShouldContain("http://127.0.0.1:5101/fhir/ (east, oracle)");
    }

    [Theory]
    [InlineData("https://east.example/fh%69r/")]
    [InlineData("https://east.example/temporary/../fhir/")]
    public void Bind_RejectsSpellingEquivalentBasesWithAmbiguousPathSegments(string westBase)
    {
        var shards = new Dictionary<string, string>
        {
            ["east"] = "https://east.example/fhir/",
            ["west"] = westBase,
        };

        var message = Should.Throw<ArgumentException>(() => new OracleBases(shards, oracle: null)).Message;

        message.ShouldContain(westBase);
    }

    [Fact]
    public void Bind_KeepsBasesDistinctByPortPathAndPathCase_AndStoresCanonicalSpelling()
    {
        var byPathCase = new OracleBases(
            new Dictionary<string, string> { ["east"] = "https://east.example/fhir/", ["west"] = "https://EAST.example:443/FHIR" },
            "https://east.example:8443/fhir");

        Should.NotThrow(() => OracleFixtures.Bind("include.forward", "colocated", byPathCase));
        byPathCase.Shards["west"].ShouldBe("https://east.example/FHIR/");
        byPathCase.Oracle.ShouldBe("https://east.example:8443/fhir/");
    }

    [Theory]
    [InlineData("east", "fhir/east")]
    [InlineData("east", "ftp://east.example/fhir/")]
    [InlineData("west", "https://user@east.example/fhir/")]
    [InlineData("west", "https://east.example/fhir/?tenant=1")]
    [InlineData("west", "https://east.example/fhir/#top")]
    [InlineData("west", "https://east.example/./fhir/")]
    [InlineData("oracle", "https://oracle.example/fh%69r/")]
    [InlineData("oracle", "urn:uuid:3f2a1c9e-0000-4000-8000-000000000001")]
    public void Bind_RejectsInvalidBaseUrl(string registration, string badBase)
    {
        var shards = new Dictionary<string, string> { ["east"] = East, ["west"] = West };
        string? oracle = OracleUrl;
        if (registration == "oracle")
        {
            oracle = badBase;
        }
        else
        {
            shards[registration] = badBase;
        }

        var message = Should.Throw<ArgumentException>(() => OracleFixtures.Bind("include.forward", "colocated", new OracleBases(shards, oracle))).Message;

        message.ShouldContain(registration);
        message.ShouldContain(badBase);
    }

    [Fact]
    public void CollisionTaggedUnion_FullUrlSpelledWithUpperCaseHostAndDefaultPort_IsTheRegisteredIdentity()
    {
        const string EastHttps = "https://east.example/fhir/";
        const string EastShouted = "https://EAST.example:443/fhir/";
        var c = OracleFixtures.Bind(
            "collision.tagged-union",
            "per-shard",
            new OracleBases(new Dictionary<string, string> { ["east"] = EastHttps, ["west"] = West }, oracle: null));
        JsonNode[] ownedUnderOtherSpelling =
        [
            Page(next: true, Match(EastShouted, "Patient", "dup", "Eli")),
            Page(next: false, Match(West, "Patient", "dup", "Gus")),
        ];
        JsonNode[] sameResourceTwice =
        [
            Page(next: true, Match(EastHttps, "Patient", "dup", "Eli")),
            Page(next: false, Match(EastShouted, "Patient", "dup", "Gus")),
        ];

        OraclePageComparator.Compare(c, ownedUnderOtherSpelling, oraclePages: null).ShouldBeEmpty();
        OraclePageComparator.Compare(c, sameResourceTwice, oraclePages: null).ShouldBe(
            [
                "[collision.tagged-union] federation: match https://east.example/fhir/Patient/dup repeated on page 1 and page 2",
                "[collision.tagged-union] federation: unexpected match https://east.example/fhir/Patient/dup (returned 2, expected 1)",
                "[collision.tagged-union] federation: missing match http://127.0.0.1:5102/Patient/dup (returned 0, expected 1)",
                "[collision.tagged-union] federation page 2: https://east.example/fhir/Patient/dup: name[0].given is [Gus], expected [Eli]",
            ],
            ignoreOrder: true);
    }

    [Theory]
    [InlineData("urn:uuid:3f2a1c9e-0000-4000-8000-000000000002")]
    [InlineData("Patient/dup")]
    [InlineData("ftp://127.0.0.1:5102/Patient/dup")]
    [InlineData("http://user@127.0.0.1:5102/Patient/dup")]
    public void FullUrlNotAnAbsoluteHttpUrl_FailsOwnership(string fullUrl)
    {
        var c = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(East, "Patient", "dup", "Eli")),
            Page(next: false, Entry("match", fullUrl, "Patient", "dup", "Gus")),
        ];

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldContain($"[collision.tagged-union] federation page 2: fullUrl {fullUrl} is not an absolute http(s) URL without userinfo");
    }

    [Theory]
    [InlineData(1, """[{"relation":"next"}]""", new[] { "link 1 (next) url is not a nonblank string", "no next link, but further pages were supplied (page list over-followed)" })]
    [InlineData(1, """[{"relation":"next","url":"  "}]""", new[] { "link 1 (next) url is not a nonblank string", "no next link, but further pages were supplied (page list over-followed)" })]
    [InlineData(1, """[{"relation":"self","url":"http://harness.invalid/self"},{"relation":"next","url":7}]""", new[] { "link 2 (next) url is not a nonblank string", "no next link, but further pages were supplied (page list over-followed)" })]
    [InlineData(2, """[{"relation":"next"}]""", new[] { "link 1 (next) url is not a nonblank string" })]
    [InlineData(2, """[{"relation":"Next","url":"http://harness.invalid/next"}]""", new[] { "last supplied page has a next link (page list truncated)" })]
    [InlineData(2, "null", new[] { "link is not an array" })]
    [InlineData(1, "null", new[] { "link is not an array", "no next link, but further pages were supplied (page list over-followed)" })]
    [InlineData(2, """{"relation":"next","url":"http://harness.invalid/next"}""", new[] { "link is not an array" })]
    [InlineData(1, """{"relation":"next","url":"http://harness.invalid/next"}""", new[] { "link is not an array", "no next link, but further pages were supplied (page list over-followed)" })]
    [InlineData(2, """[{"relation":"next","url":"a"},{"relation":"next","url":"b"}]""", new[] { "2 next links, expected at most one", "last supplied page has a next link (page list truncated)" })]
    [InlineData(2, """[{"url":"http://harness.invalid/self"},"next"]""", new[] { "link 1 is not an object with a string relation", "link 2 is not an object with a string relation" })]
    public void PagingEvidenceMalformed_FailsNamingThePage(int malformedPage, string link, string[] problems)
    {
        var c = OracleFixtures.Bind("collision.tagged-union", "per-shard", Bases);
        JsonNode[] federation =
        [
            Page(next: true, Match(East, "Patient", "dup", "Eli")),
            Page(next: false, Match(West, "Patient", "dup", "Gus")),
        ];
        var bundle = (JsonObject)federation[malformedPage - 1];
        bundle["link"] = JsonNode.Parse(link);
        if (link == "null")
        {
            bundle.TryGetPropertyValue("link", out var parsedLink).ShouldBeTrue();
            parsedLink.ShouldBeNull();
        }

        var failures = OraclePageComparator.Compare(c, federation, oraclePages: null);

        failures.ShouldBe([.. problems.Select(problem => $"[collision.tagged-union] federation page {malformedPage}: {problem}")]);
    }

    private static JsonNode[] CrossShardIncludeForwardFederation() =>
    [
        Page(next: true, Match(West, "Observation", "obs-cedar-1"), Include(West, "Patient", "p-cedar")),
        Page(next: true, Match(East, "Observation", "obs-orchid-1"), Include(East, "Patient", "p-orchid")),
        Page(next: false, Match(West, "Observation", "obs-orchid-2"), Include(East, "Patient", "p-orchid")),
    ];

    private static JsonNode[] IncludeForwardOracle() =>
    [
        Page(next: true, Match(OracleUrl, "Observation", "obs-orchid-2"), Include(OracleUrl, "Patient", "p-orchid")),
        Page(next: true, Match(OracleUrl, "Observation", "obs-cedar-1"), Include(OracleUrl, "Patient", "p-cedar")),
        Page(next: false, Match(OracleUrl, "Observation", "obs-orchid-1"), Include(OracleUrl, "Patient", "p-orchid")),
    ];

    private static void ShouldReport(IReadOnlyList<string> failures, params string[] fragments) =>
        failures.Any(f => fragments.All(f.Contains)).ShouldBeTrue(
            $"expected a failure containing [{string.Join(" | ", fragments)}]; actual failures:{Environment.NewLine}"
            + string.Join(Environment.NewLine, failures));

    private static JsonObject Match(string baseUrl, string resourceType, string id, params string[] given) =>
        Entry("match", baseUrl + resourceType + "/" + id, resourceType, id, given);

    private static JsonObject Include(string baseUrl, string resourceType, string id, params string[] given) =>
        Entry("include", baseUrl + resourceType + "/" + id, resourceType, id, given);

    private static JsonObject Entry(string mode, string fullUrl, string resourceType, string id, params string[] given)
    {
        var resource = new JsonObject { ["resourceType"] = resourceType, ["id"] = id, ["meta"] = new JsonObject { ["versionId"] = "1" } };
        if (given.Length > 0)
        {
            resource["name"] = new JsonArray(new JsonObject { ["family"] = "Orchid", ["given"] = new JsonArray([.. given.Select(g => JsonValue.Create(g))]) });
        }

        return new JsonObject { ["fullUrl"] = fullUrl, ["resource"] = resource, ["search"] = new JsonObject { ["mode"] = mode } };
    }

    private static JsonNode Page(bool next, params JsonObject[] entries)
    {
        var links = new JsonArray(new JsonObject { ["relation"] = "self", ["url"] = "http://harness.invalid/self" });
        if (next)
        {
            links.Add(new JsonObject { ["relation"] = "next", ["url"] = "http://harness.invalid/next" });
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "searchset",
            ["link"] = links,
            ["entry"] = new JsonArray([.. entries]),
        };
    }
}

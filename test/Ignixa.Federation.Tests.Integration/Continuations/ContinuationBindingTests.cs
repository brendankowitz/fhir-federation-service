using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// The caller binding and dependency inputs (design spec §7.4): exact, unambiguous encodings of validated
/// identifiers, observed through issue and redemption rather than through the hash itself.
/// </summary>
public sealed class ContinuationBindingTests : IDisposable
{
    private readonly IsolatedKeyRing _ring = new();
    private readonly RevisionCatalog _revisions = new();
    private readonly ContinuationProtector _protector;

    public ContinuationBindingTests() => _protector = new(_ring.CreateProvider(), new TokenOptions(), new SettableClock(Start));

    // Pairs that a naive delimiter-joined or concatenated encoding would map to the same caller.
    public static TheoryData<string> Collisions =>
    [
        "issuer|subject boundary",
        "subject|id boundary",
        "comma inside a role",
        "role concatenation",
        "empty role",
        "duplicate role",
        "object id as client id",
    ];

    public void Dispose() => _ring.Dispose();

    private static (ContinuationBinding Issued, ContinuationBinding Presented) CollidingPair(string collision) => collision switch
    {
        "issuer|subject boundary" => (
            ContinuationBinding.Create("iss|a", "b", ObjectId, null, ["Reader"]),
            ContinuationBinding.Create("iss", "a|b", ObjectId, null, ["Reader"])),
        "subject|id boundary" => (
            ContinuationBinding.Create(Issuer, "sub", "x-oid", null, ["Reader"]),
            ContinuationBinding.Create(Issuer, "subx", "-oid", null, ["Reader"])),
        "comma inside a role" => (Caller("Reader,Auditor"), Caller("Reader", "Auditor")),
        "role concatenation" => (Caller("ReaderAuditor"), Caller("Reader", "Auditor")),
        "empty role" => (
            ContinuationBinding.Create(Issuer, Subject, ObjectId, null, [""]),
            ContinuationBinding.Create(Issuer, Subject, ObjectId, null, [])),
        "duplicate role" => (Caller("Reader", "Reader"), Caller("Reader")),
        "object id as client id" => (
            ContinuationBinding.Create(Issuer, Subject, "app-1", null, ["Reader"]),
            ContinuationBinding.Create(Issuer, Subject, null, "app-1", ["Reader"])),
        _ => throw new ArgumentOutOfRangeException(nameof(collision)),
    };

    [Theory]
    [MemberData(nameof(Collisions))]
    public void Callers_ThatOnlyCollideUnderANaiveEncoding_AreDistinct(string collision)
    {
        (ContinuationBinding issued, ContinuationBinding presented) = CollidingPair(collision);
        string token = _protector.Issue(issued, Dependencies(), Snapshot(4)).ShouldBeInline();

        _protector.Redeem(token, presented, _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
        _protector.Redeem(token, issued, _revisions.Lookup).ShouldBeRedeemed();
    }

    [Fact]
    public void ClientId_IsTheFallback_AndIsNotBoundWhenAnObjectIdIsPresent()
    {
        string withObjectId = _protector.Issue(
            ContinuationBinding.Create(Issuer, Subject, ObjectId, "client-1", ["Reader"]), Dependencies(), Snapshot(4)).ShouldBeInline();
        string withClientIdOnly = _protector.Issue(
            ContinuationBinding.Create(Issuer, Subject, null, "client-1", ["Reader"]), Dependencies(), Snapshot(4)).ShouldBeInline();

        _protector.Redeem(withObjectId, ContinuationBinding.Create(Issuer, Subject, ObjectId, "client-2", ["Reader"]), _revisions.Lookup)
            .ShouldBeRedeemed();
        _protector.Redeem(withClientIdOnly, ContinuationBinding.Create(Issuer, Subject, null, "client-2", ["Reader"]), _revisions.Lookup)
            .ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
    }

    [Fact]
    public void Roles_AreComparedOrdinally_NotByCultureOrNormalisation()
    {
        // "é" precomposed versus "e" + combining acute: equal under normalisation, different ordinally.
        string token = _protector.Issue(Caller("R\u00E9viewer"), Dependencies(), Snapshot(4)).ShouldBeInline();

        _protector.Redeem(token, Caller("Re\u0301viewer"), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
    }

    [Fact]
    public void Binding_WithALoneSurrogate_IsRefused_RatherThanCollapsedToAReplacementCharacter()
    {
        Should.Throw<ArgumentException>(() => Caller("Reader\uD800"));
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(Issuer, "sub\uDC00", ObjectId, null, ["Reader"]));

        string token = _protector.Issue(Caller("Reader\uFFFD"), Dependencies(), Snapshot(4)).ShouldBeInline();
        _protector.Redeem(token, Caller("Reader\uFFFD"), _revisions.Lookup).ShouldBeRedeemed();
    }

    [Fact]
    public void Binding_WithoutRequiredIdentifiers_IsRefused()
    {
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(Issuer, Subject, null, null, ["Reader"]));
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(" ", Subject, ObjectId, null, ["Reader"]));
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(Issuer, "", ObjectId, null, ["Reader"]));
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(Issuer, Subject, "", "client-1", ["Reader"]));
        Should.Throw<ArgumentException>(() => ContinuationBinding.Create(Issuer, Subject, ObjectId, null, ["Reader", null!]));
    }

    [Fact]
    public void Dependencies_WithADuplicateKeyInOneGroup_AreRefused()
    {
        Should.Throw<ArgumentException>(() => ContinuationDependencySet.Create([new(ShardA, "r1"), new(ShardA, "r2")], []));
        Should.Throw<ArgumentException>(() => ContinuationDependencySet.Create([], [new(CodeDefinition, "d1"), new(CodeDefinition, "d1")]));
    }

    [Fact]
    public void Dependencies_WithBlankOrIllFormedValues_AreRefused()
    {
        Should.Throw<ArgumentException>(() => ContinuationDependencySet.Create([new("", "r1")], []));
        Should.Throw<ArgumentException>(() => ContinuationDependencySet.Create([new(ShardA, "")], []));
        Should.Throw<ArgumentException>(() => ContinuationDependencySet.Create([], [new("def\uD800", "d1")]));
    }

    [Fact]
    public void Dependencies_MayUseTheSameKeyInBothGroups_AsSeparateNamespaces()
    {
        ContinuationDependencySet dependencies = ContinuationDependencySet.Create([new("shared", "shard-rev")], [new("shared", "def-rev")]);
        _revisions.Set(ContinuationDependencyGroup.Shard, "shared", "shard-rev");
        _revisions.Set(ContinuationDependencyGroup.Definition, "shared", "def-rev");
        string token = _protector.Issue(Caller(), dependencies, Snapshot(4)).ShouldBeInline();

        _protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed();

        _revisions.Set(ContinuationDependencyGroup.Definition, "shared", "def-rev-2");
        _protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }
}

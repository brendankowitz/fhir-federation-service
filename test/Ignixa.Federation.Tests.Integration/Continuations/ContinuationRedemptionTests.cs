using System.Buffers.Text;
using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Redemption semantics (design spec §7.4) across independent protectors over real Data Protection key rings:
/// immutable repeat redemption, key ring and purpose isolation, the caller binding, and invalidation by only the
/// authenticated dependencies. Failure precedence is invalid (400), caller mismatch (403), then gone (410).
/// </summary>
public sealed class ContinuationRedemptionTests : IDisposable
{
    private readonly IsolatedKeyRing _ring = new();
    private readonly SettableClock _clock = new(Start);
    private readonly RevisionCatalog _revisions = new();

    public static TheoryData<string> ChangedCallers =>
    [
        "issuer",
        "subject",
        "object id",
        "object id presented as client id",
        "role added",
        "role removed",
        "role case changed",
    ];

    public static TheoryData<string> RelevantDependencies => [ShardA, ShardB, CodeDefinition];

    public void Dispose() => _ring.Dispose();

    private ContinuationProtector CreateProtector(string applicationName = IsolatedKeyRing.ApplicationName) =>
        new(_ring.CreateProvider(applicationName), new TokenOptions(), _clock);

    private static ContinuationDependencyGroup GroupOf(string key) =>
        key == CodeDefinition ? ContinuationDependencyGroup.Definition : ContinuationDependencyGroup.Shard;

    private static string IssueInitial(ContinuationProtector protector) =>
        protector.Issue(Caller(), Dependencies(), Snapshot(32)).ShouldBeInline();

    private static ContinuationBinding ChangedCaller(string change) => change switch
    {
        "issuer" => ContinuationBinding.Create(Issuer + "/", Subject, ObjectId, null, ["Reader", "Auditor"]),
        "subject" => ContinuationBinding.Create(Issuer, "subject-b", ObjectId, null, ["Reader", "Auditor"]),
        "object id" => ContinuationBinding.Create(Issuer, Subject, "object-b", null, ["Reader", "Auditor"]),
        "object id presented as client id" => ContinuationBinding.Create(Issuer, Subject, null, ObjectId, ["Reader", "Auditor"]),
        "role added" => Caller("Reader", "Auditor", "Writer"),
        "role removed" => Caller("Reader"),
        "role case changed" => Caller("reader", "Auditor"),
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };

    [Fact]
    public void Redeem_TheSameTokenRepeatedly_ReturnsTheSameSnapshotEachTime()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        RedeemedContinuation first = protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed();
        RedeemedContinuation second = protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed();

        first.Snapshot.ShouldBe(Snapshot(32));
        second.Snapshot.ShouldBe(Snapshot(32));
        first.ShouldNotBeSameAs(second);
    }

    [Fact]
    public void Redeem_AcrossIndependentProtectorsSharingTheKeyRing_SucceedsInBothDirections()
    {
        ContinuationProtector instanceA = CreateProtector();
        ContinuationProtector instanceB = CreateProtector();

        string fromA = IssueInitial(instanceA);
        RedeemedContinuation onB = instanceB.Redeem(fromA, Caller(), _revisions.Lookup).ShouldBeRedeemed();
        string successorFromB = instanceB.IssueSuccessor(onB, Snapshot(24, seed: 3)).ShouldBeInline();

        instanceA.Redeem(successorFromB, Caller(), _revisions.Lookup).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(24, seed: 3));
        instanceA.Redeem(fromA, Caller(), _revisions.Lookup).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(32));
    }

    [Fact]
    public void Redeem_UnderAnotherApplicationDiscriminator_IsInvalid()
    {
        string token = IssueInitial(CreateProtector());

        CreateProtector("Ignixa.Federation.Tests.OtherDeployment")
            .Redeem(token, Caller(), _revisions.Lookup)
            .ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void Redeem_AgainstAnotherKeyRing_IsInvalid()
    {
        string token = IssueInitial(CreateProtector());
        using var otherRing = new IsolatedKeyRing();

        new ContinuationProtector(otherRing.CreateProvider(), new TokenOptions(), _clock)
            .Redeem(token, Caller(), _revisions.Lookup)
            .ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void Redeem_ContentsProtectedUnderAnotherPurposeOfTheSameRing_IsInvalid()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider provider = _ring.CreateProvider();
        byte[] contents = provider.CreateProtector(ContinuationProtector.Purpose).Unprotect(Base64Url.DecodeFromChars(token));

        string samePurpose = Base64Url.EncodeToString(provider.CreateProtector(ContinuationProtector.Purpose).Protect(contents));
        string otherPurpose = Base64Url.EncodeToString(provider.CreateProtector("Ignixa.Federation.Tests.OtherPurpose").Protect(contents));

        protector.Redeem(samePurpose, Caller(), _revisions.Lookup).ShouldBeRedeemed();
        protector.Redeem(otherPurpose, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
    }

    [Theory]
    [MemberData(nameof(ChangedCallers))]
    public void Redeem_ByAChangedCaller_IsCallerMismatch_WithoutConsultingRevisions(string change)
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        protector.Redeem(token, ChangedCaller(change), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);

        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void Redeem_WithTheSameRolesInAnotherOrder_IsRedeemed()
    {
        ContinuationProtector protector = CreateProtector();
        string token = protector.Issue(Caller("Reader", "Auditor", "Analyst"), Dependencies(), Snapshot(4)).ShouldBeInline();

        protector.Redeem(token, Caller("Analyst", "Reader", "Auditor"), _revisions.Lookup).ShouldBeRedeemed();
    }

    [Fact]
    public void Redeem_AnExpiredTokenByAChangedCaller_IsCallerMismatch()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);
        _clock.Now = Start + TimeSpan.FromHours(2);

        protector.Redeem(token, ChangedCaller("subject"), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
    }

    [Fact]
    public void Redeem_AnExpiredToken_IsGone_WithoutConsultingRevisions()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);
        _revisions.Set(ContinuationDependencyGroup.Shard, ShardA, "rev-a2");
        _clock.Now = Start + TimeSpan.FromMinutes(15);

        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);

        _revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void Redeem_WhenNoTokenWasPresented_IsGone()
    {
        CreateProtector().Redeem(null, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);

        _revisions.Consulted.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(RelevantDependencies))]
    public void Redeem_AfterARelevantDependencyChanged_IsGone(string key)
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        _revisions.Set(GroupOf(key), key, "changed");

        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Theory]
    [MemberData(nameof(RelevantDependencies))]
    public void Redeem_AfterARelevantDependencyDisappeared_IsGone(string key)
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        _revisions.Remove(GroupOf(key), key);

        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void Redeem_ConsultsExactlyTheAuthenticatedDependencies_AndIgnoresUnrelatedChanges()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        // A new shard, an unrelated definition, and keys that name a dependency of the token in the other group.
        _revisions.Set(ContinuationDependencyGroup.Shard, "shard-c", "rev-c1");
        _revisions.Set(ContinuationDependencyGroup.Definition, "http://hl7.org/fhir/SearchParameter/unrelated", "def-9");
        _revisions.Set(ContinuationDependencyGroup.Definition, ShardA, "def-x");
        _revisions.Set(ContinuationDependencyGroup.Shard, CodeDefinition, "rev-x");

        protector.Redeem(token, Caller(), _revisions.Lookup).ShouldBeRedeemed();

        _revisions.Consulted.ShouldBe(
            [
                (ContinuationDependencyGroup.Shard, ShardA),
                (ContinuationDependencyGroup.Shard, ShardB),
                (ContinuationDependencyGroup.Definition, CodeDefinition),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Successor_RetainsThePredecessorsAuthenticatedDependencies()
    {
        ContinuationProtector protector = CreateProtector();
        RedeemedContinuation redeemed = protector.Redeem(IssueInitial(protector), Caller(), _revisions.Lookup).ShouldBeRedeemed();
        string successor = protector.IssueSuccessor(redeemed, Snapshot(8)).ShouldBeInline();

        _revisions.Set(ContinuationDependencyGroup.Shard, "shard-c", "rev-c1");
        protector.Redeem(successor, Caller(), _revisions.Lookup).ShouldBeRedeemed();

        _revisions.Set(ContinuationDependencyGroup.Definition, CodeDefinition, "def-2");
        protector.Redeem(successor, Caller(), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void Successor_RemainsBoundToTheOriginalCaller()
    {
        ContinuationProtector protector = CreateProtector();
        RedeemedContinuation redeemed = protector.Redeem(IssueInitial(protector), Caller(), _revisions.Lookup).ShouldBeRedeemed();
        string successor = protector.IssueSuccessor(redeemed, Snapshot(8)).ShouldBeInline();

        protector.Redeem(successor, ChangedCaller("role removed"), _revisions.Lookup).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
        protector.Redeem(successor, Caller(), _revisions.Lookup).ShouldBeRedeemed();
    }

    [Fact]
    public void Results_DoNotRenderTokensOrProtectedContents()
    {
        ContinuationProtector protector = CreateProtector();
        ContinuationIssue issue = protector.Issue(Caller(), Dependencies(), Snapshot(32));
        string token = issue.ShouldBeInline();
        ContinuationRedemption redemption = protector.Redeem(token, Caller(), _revisions.Lookup);

        string rendered = string.Join(
            "\n",
            issue.ToString(),
            redemption.ToString(),
            redemption.ShouldBeRedeemed().ToString(),
            Caller().ToString(),
            Dependencies().ToString());

        rendered.ShouldNotContain(token);
        foreach (string sensitive in new[] { Issuer, Subject, ObjectId, "Reader", ShardA, CodeDefinition, "rev-a1", "def-1" })
        {
            rendered.ShouldNotContain(sensitive);
        }
    }
}

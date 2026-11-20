using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// The §7.4 lifetime (D5) under a controlled wall clock: each token is valid in <c>[issued, deadline)</c> where the
/// deadline is the earlier of its sliding expiry and the chain's absolute expiry. Real Data Protection throughout.
/// </summary>
public sealed class ContinuationLifetimeTests : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly IsolatedKeyRing _ring = new();
    private readonly SettableClock _clock = new(Start);
    private readonly RevisionCatalog _revisions = new();

    public void Dispose() => _ring.Dispose();

    private ContinuationProtector CreateProtector(TokenOptions? options = null, TimeProvider? clock = null) =>
        new(_ring.CreateProvider(), options ?? new TokenOptions(), clock ?? _clock);

    private static string IssueInitial(ContinuationProtector protector) =>
        protector.Issue(Caller(), Dependencies(), Snapshot(16)).ShouldBeInline();

    private ContinuationRedemption Redeem(ContinuationProtector protector, string token) =>
        protector.Redeem(token, Caller(), _revisions.Lookup);

    private void At(TimeSpan sinceStart) => _clock.Now = Start + sinceStart;

    [Fact]
    public void Token_WithDefaultOptions_IsRedeemableFromItsIssueInstantUntilJustBeforeFifteenMinutes()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        Redeem(protector, token).ShouldBeRedeemed();

        At(TimeSpan.FromMinutes(15) - Tick);
        Redeem(protector, token).ShouldBeRedeemed();

        At(TimeSpan.FromMinutes(15));
        Redeem(protector, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void Token_RedeemedBeforeItsIssueInstant_IsGone()
    {
        var issuerClock = new SettableClock(Start + TimeSpan.FromMinutes(1));
        string token = IssueInitial(CreateProtector(clock: issuerClock));
        ContinuationProtector redeemer = CreateProtector();

        At(TimeSpan.FromMinutes(1) - Tick);
        Redeem(redeemer, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);

        At(TimeSpan.FromMinutes(1));
        Redeem(redeemer, token).ShouldBeRedeemed();
    }

    [Fact]
    public void Successors_SlideTheWindow_ButNeverPastSixtyMinutesFromTheOriginalIssue()
    {
        ContinuationProtector protector = CreateProtector();
        string token = IssueInitial(protector);

        foreach (int minutes in new[] { 10, 20, 30, 40, 50 })
        {
            At(TimeSpan.FromMinutes(minutes));
            RedeemedContinuation redeemed = Redeem(protector, token).ShouldBeRedeemed();
            token = protector.IssueSuccessor(redeemed, Snapshot(16, seed: (byte)minutes)).ShouldBeInline();
        }

        // Issued at 50 minutes, the last successor would slide to 65 minutes; the chain started at 0 caps it at 60.
        At(TimeSpan.FromMinutes(60) - Tick);
        Redeem(protector, token).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(16, seed: 50));

        At(TimeSpan.FromMinutes(60));
        Redeem(protector, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void Predecessor_AfterASuccessorIsIssued_StaysRedeemableWithinItsOwnWindowOnly()
    {
        ContinuationProtector protector = CreateProtector();
        string first = IssueInitial(protector);

        At(TimeSpan.FromMinutes(10));
        RedeemedContinuation redeemed = Redeem(protector, first).ShouldBeRedeemed();
        string second = protector.IssueSuccessor(redeemed, Snapshot(16, seed: 99)).ShouldBeInline();

        At(TimeSpan.FromMinutes(15) - Tick);
        Redeem(protector, first).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(16));

        At(TimeSpan.FromMinutes(15));
        Redeem(protector, first).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        Redeem(protector, second).ShouldBeRedeemed();

        At(TimeSpan.FromMinutes(25));
        Redeem(protector, second).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void ConfiguredLifetimes_ReplaceTheDefaults()
    {
        var options = new TokenOptions { SlidingLifetime = TimeSpan.FromMinutes(2), AbsoluteLifetime = TimeSpan.FromMinutes(5) };
        ContinuationProtector protector = CreateProtector(options);
        string first = IssueInitial(protector);

        At(TimeSpan.FromMinutes(2) - Tick);
        Redeem(protector, first).ShouldBeRedeemed();
        At(TimeSpan.FromMinutes(2));
        Redeem(protector, first).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);

        // Successors every 90 seconds; the one issued at 4.5 minutes would slide to 6.5 but the chain caps it at 5.
        string token = first;
        foreach (int seconds in new[] { 90, 180, 270 })
        {
            At(TimeSpan.FromSeconds(seconds));
            token = protector.IssueSuccessor(Redeem(protector, token).ShouldBeRedeemed(), Snapshot(8)).ShouldBeInline();
        }

        At(TimeSpan.FromMinutes(5) - Tick);
        Redeem(protector, token).ShouldBeRedeemed();
        At(TimeSpan.FromMinutes(5));
        Redeem(protector, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void Options_ChangedAfterConstruction_DoNotChangeTheLifetime()
    {
        var options = new TokenOptions();
        ContinuationProtector protector = CreateProtector(options);
        options.SlidingLifetime = TimeSpan.FromMinutes(1);
        options.AbsoluteLifetime = TimeSpan.FromMinutes(1);

        string token = IssueInitial(protector);
        At(TimeSpan.FromMinutes(15) - Tick);

        Redeem(protector, token).ShouldBeRedeemed();
    }

    [Fact]
    public void AReaderWithLongerLifetimes_DoesNotExtendAnIssuedTokensWindow()
    {
        string token = IssueInitial(CreateProtector());
        ContinuationProtector reader = CreateProtector(new TokenOptions
        {
            SlidingLifetime = TimeSpan.FromMinutes(30),
            AbsoluteLifetime = TimeSpan.FromMinutes(90),
        });

        At(TimeSpan.FromMinutes(15) - Tick);
        Redeem(reader, token).ShouldBeRedeemed();
        At(TimeSpan.FromMinutes(15));
        Redeem(reader, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public void SuccessorOnAnInstanceWithLongerOptions_KeepsTheOriginalChainDeadline()
    {
        string first = IssueInitial(CreateProtector());
        ContinuationProtector reader = CreateProtector(new TokenOptions
        {
            SlidingLifetime = TimeSpan.FromMinutes(30),
            AbsoluteLifetime = TimeSpan.FromMinutes(90),
        });

        string token = first;
        foreach (int minute in new[] { 10, 30, 50 })
        {
            At(TimeSpan.FromMinutes(minute));
            token = reader.IssueSuccessor(Redeem(reader, token).ShouldBeRedeemed(), Snapshot(8)).ShouldBeInline();
        }

        At(TimeSpan.FromMinutes(60) - Tick);
        Redeem(reader, token).ShouldBeRedeemed();
        At(TimeSpan.FromMinutes(60));
        Redeem(reader, token).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }
}

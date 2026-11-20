using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.CachedContinuationRig;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;
using Operation = Ignixa.Federation.Tests.Integration.Continuations.ContractContinuationCache.Operation;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Successors of cached references: the successor is durable before the predecessor's entry is trimmed to the grace
/// window, the trim only ever shortens, and every failure leaves a state that expires on its own.
/// </summary>
public sealed class CachedContinuationSuccessorTests : IDisposable
{
    private static readonly TimeSpan Grace = ContinuationTokenService.PredecessorGrace;
    private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(15);
    private readonly CachedContinuationRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task CachedSuccessor_TrimsThePredecessorToTheGrace_AfterWhichOnlyTheSuccessorRedeems()
    {
        (string predecessor, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(2));

        string successor = (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeIssued();

        IsReference(successor).ShouldBeTrue();
        DateTimeOffset now = Start + TimeSpan.FromMinutes(2);
        _rig.Store.ExpiryOf(key).ShouldBe(now + Grace);
        _rig.Store.ExpiryOf(_rig.Store.Written[^1]).ShouldBe(now + Sliding);

        _rig.Clock.Now = now + Grace - TimeSpan.FromTicks(1);
        (await _rig.RedeemAsync(_rig.Service(), predecessor)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(CachedSnapshotLength));
        _rig.Clock.Now = now + Grace;
        (await _rig.RedeemAsync(_rig.Service(), predecessor)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        (await _rig.RedeemAsync(_rig.Service(), successor)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(CachedSnapshotLength, 2));
    }

    [Fact]
    public async Task InlineSuccessorOfACachedPredecessor_StillTrimsIt()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(3));

        string successor = (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(64), Ct)).ShouldBeIssued();

        IsReference(successor).ShouldBeFalse();
        _rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromMinutes(3) + Grace);
        _rig.Store.Sets.ShouldBe(1);
    }

    [Fact]
    public async Task InlinePredecessor_IsNeitherTrimmedNorShortened()
    {
        ContinuationTokenService service = _rig.Service();
        string inline = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(64), Ct)).ShouldBeIssued();
        _rig.At(TimeSpan.FromMinutes(1));
        RedeemedContinuation redeemed = (await _rig.RedeemAsync(service, inline)).ShouldBeRedeemed();

        string successor = (await service.IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength), Ct)).ShouldBeIssued();

        IsReference(successor).ShouldBeTrue();
        _rig.Store.Shortens.ShouldBe(0);
        _rig.At(Sliding - TimeSpan.FromTicks(1));
        (await _rig.RedeemAsync(service, inline)).ShouldBeRedeemed();
    }

    [Fact]
    public async Task OversizeSuccessor_IsTooLarge_WithoutWritingOrTrimming()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(ContinuationTokenService.DefaultMaxCachedPlaintextBytes), Ct))
            .ShouldBeRefused(ContinuationIssuanceOutcome.TooLarge);

        (_rig.Store.Sets, _rig.Store.Shortens).ShouldBe((1, 0));
        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutSuccessorWrite_IsStoreUnavailable_AndLeavesThePredecessorWhole(bool timeout)
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (operation, token) =>
        {
            operation.ShouldBe(Operation.Set);
            if (!timeout)
            {
                throw new ContinuationCacheUnavailableException();
            }

            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct);
        if (timeout)
        {
            await entered.Task;
            _rig.At(TimeSpan.FromMinutes(1) + ContinuationTokenService.StoreTimeout);
        }

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.Shortens.ShouldBe(0);
        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationDuringOrJustAfterTheWrite_Propagates_WithoutTrimming(bool afterWrite)
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        using var caller = new CancellationTokenSource();
        if (afterWrite)
        {
            _rig.Store.AfterApply = _ => caller.CancelAsync();
        }
        else
        {
            _rig.Store.BeforeApply = async (_, token) =>
            {
                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            };
        }

        await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), caller.Token).ShouldBeCanceledAsync();

        _rig.Store.Shortens.ShouldBe(0);
        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
    }

    [Fact]
    public async Task CallerCancellationOnceTheTrimHasStarted_DoesNotAbandonIt()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        using var caller = new CancellationTokenSource();
        _rig.Store.BeforeApply = async (operation, token) =>
        {
            if (operation == Operation.Shorten)
            {
                await caller.CancelAsync();
                token.IsCancellationRequested.ShouldBeFalse();
            }
        };

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), caller.Token)).ShouldBeIssued();
        _rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromMinutes(1) + Grace);
    }

    [Fact]
    public async Task TrimThatOutlivesTheStoreTimeout_IsStoreUnavailable()
    {
        (_, _, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (operation, token) =>
        {
            if (operation == Operation.Shorten)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct);
        await entered.Task;
        _rig.At(TimeSpan.FromMinutes(1) + ContinuationTokenService.StoreTimeout);

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
    }

    [Fact]
    public async Task TrimThatIgnoresItsTokenAndSucceedsAfterTheStoreTimeout_IsStoreUnavailable()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (operation, _) =>
        {
            if (operation == Operation.Shorten)
            {
                entered.SetResult();
                await release.Task;
            }
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct);
        await entered.Task;
        _rig.At(TimeSpan.FromMinutes(1) + ContinuationTokenService.StoreTimeout);
        release.SetResult();

        // The late shortening took effect; only shortening, it is as safe to have applied as to retry.
        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromMinutes(1) + ContinuationTokenService.StoreTimeout + Grace);
    }

    [Fact]
    public async Task SuccessorConfirmedOnlyAfterItsOwnDeadline_IsGone_WithoutTrimming()
    {
        // A shorter-lived instance issues the successor; the lagging store keeps the late entry, but the successor's
        // authenticated deadline has passed (within the store timeout), so it is never handed out and nothing is trimmed.
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        rig.At(TimeSpan.FromMinutes(1));
        RedeemedContinuation redeemed = (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRedeemed();
        var shortLived = new TokenOptions { SlidingLifetime = TimeSpan.FromSeconds(2) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationIssuance> issuing = rig.Service(shortLived).IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct);
        await entered.Task;
        rig.At(TimeSpan.FromMinutes(1) + shortLived.SlidingLifetime);
        release.SetResult();

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        rig.Store.Shortens.ShouldBe(0);
        rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
        rig.Store.ExpiryOf(rig.Store.Written[^1]).ShouldBe(Start + TimeSpan.FromMinutes(1) + shortLived.SlidingLifetime);
    }

    [Fact]
    public async Task SuccessorAlreadyExpiredByTheStoreClock_IsGone_AndThePredecessorIsNotTrimmed()
    {
        // The store's clock runs ahead of a shorter-lived instance's, so the successor's deadline has passed there when
        // its write linearizes. The store refuses it; the predecessor is still valid and keeps its whole lifetime.
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        rig.At(TimeSpan.FromMinutes(1));
        RedeemedContinuation redeemed = (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRedeemed();
        var shortLived = new TokenOptions { SlidingLifetime = TimeSpan.FromSeconds(2) };
        backend.Now = Start + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(3);

        (await rig.Service(shortLived).IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct))
            .ShouldBeRefused(ContinuationIssuanceOutcome.Gone);

        rig.Store.Sets.ShouldBe(2);
        rig.Store.Written.ShouldBe([key]);
        rig.Store.Shortens.ShouldBe(0);
        rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
        (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRedeemed();
    }

    [Fact]
    public async Task UncertainTrim_IsStoreUnavailable_AndARetryWithinTheGraceNeverExtendsIt()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        DateTimeOffset trimmed = Start + TimeSpan.FromMinutes(1) + Grace;
        _rig.Store.AfterApply = operation => operation == Operation.Shorten ? throw new ContinuationCacheUnavailableException() : Task.CompletedTask;

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.ExpiryOf(key).ShouldBe(trimmed);

        _rig.Store.AfterApply = null;
        _rig.At(TimeSpan.FromMinutes(1) + (Grace / 2));
        string retried = (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeIssued();

        _rig.Store.ExpiryOf(key).ShouldBe(trimmed);
        (await _rig.RedeemAsync(_rig.Service(), retried)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(CachedSnapshotLength, 2));
    }

    [Fact]
    public async Task EvictedPredecessor_IsGone_IsNotResurrected_AndTheUnreferencedSuccessorExpiresOnItsOwn()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(1));
        _rig.Store.Evict(key);

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);

        _rig.Store.Contains(key).ShouldBeFalse();
        _rig.Store.ExpiryOf(_rig.Store.Written[^1]).ShouldBe(Start + TimeSpan.FromMinutes(1) + Sliding);
    }

    [Fact]
    public async Task PredecessorExpiredByTheStoreClockAhead_IsGone()
    {
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        rig.At(TimeSpan.FromMinutes(1));
        RedeemedContinuation redeemed = (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRedeemed();
        backend.Now = Start + Sliding;

        (await rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        rig.Store.Contains(key).ShouldBeFalse();
    }

    [Fact]
    public async Task PredecessorPastItsOwnDeadline_IsGone_WithoutWriting()
    {
        (_, _, RedeemedContinuation redeemed) = await RedeemCachedAt(TimeSpan.FromMinutes(14));
        _rig.At(Sliding);

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(64), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        (_rig.Store.Sets, _rig.Store.Shortens).ShouldBe((1, 0));
    }

    [Fact]
    public async Task InlinePredecessorPastItsOwnDeadline_IsGone()
    {
        ContinuationTokenService service = _rig.Service();
        string inline = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(64), Ct)).ShouldBeIssued();
        RedeemedContinuation redeemed = (await _rig.RedeemAsync(service, inline)).ShouldBeRedeemed();
        _rig.At(Sliding);

        (await service.IssueSuccessorAsync(redeemed, Snapshot(64), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
    }

    [Fact]
    public async Task PredecessorDeadlinePassingDuringTheSuccessorWrite_IsGone_WithoutTrimming()
    {
        // The deadline passes within the store timeout, so it is the deadline and not the timeout that refuses it.
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(Sliding - TimeSpan.FromSeconds(2));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct);
        await entered.Task;
        _rig.At(Sliding);
        release.SetResult();

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        _rig.Store.Shortens.ShouldBe(0);
        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
    }

    [Fact]
    public async Task NaturallyShorterRemainingLifetime_IsNeverExtended()
    {
        (_, string key, RedeemedContinuation redeemed) = await RedeemCachedAt(Sliding - (Grace / 2));

        (await _rig.Service().IssueSuccessorAsync(redeemed, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeIssued();

        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
    }

    [Fact]
    public async Task ConcurrentTrimsFromInstancesAtDifferentTimes_KeepTheEarliestExpiry()
    {
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        rig.At(TimeSpan.FromMinutes(1));
        ContinuationTokenService first = rig.Service();
        ContinuationTokenService second = rig.Service();
        RedeemedContinuation fromFirst = (await rig.RedeemAsync(first, reference)).ShouldBeRedeemed();
        RedeemedContinuation fromSecond = (await rig.RedeemAsync(second, reference)).ShouldBeRedeemed();

        var gates = new List<TaskCompletionSource>();
        using var arrived = new SemaphoreSlim(0);
        rig.Store.BeforeApply = async (operation, _) =>
        {
            if (operation == Operation.Shorten)
            {
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gates)
                {
                    gates.Add(gate);
                }

                arrived.Release();
                await gate.Task;
            }
        };

        Task<ContinuationIssuance> a = first.IssueSuccessorAsync(fromFirst, Snapshot(CachedSnapshotLength, 2), Ct);
        Task<ContinuationIssuance> b = second.IssueSuccessorAsync(fromSecond, Snapshot(CachedSnapshotLength, 3), Ct);
        await arrived.WaitAsync(Ct);
        await arrived.WaitAsync(Ct);

        backend.Now = Start + TimeSpan.FromSeconds(70);
        gates[1].SetResult();
        await Task.WhenAny(a, b);
        backend.Now = Start + TimeSpan.FromSeconds(100);
        gates[0].SetResult();

        (await a).ShouldBeIssued();
        (await b).ShouldBeIssued();
        rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromSeconds(70) + Grace);

        rig.Store.BeforeApply = null;
        backend.Now = Start + TimeSpan.FromSeconds(110);
        (await rig.Service().IssueSuccessorAsync(fromFirst, Snapshot(CachedSnapshotLength, 4), Ct)).ShouldBeIssued();
        rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromSeconds(70) + Grace);
    }

    [Fact]
    public async Task ChainDeadline_HoldsAcrossInlineAndCachedLinks_AndInstancesWithLongerOptions()
    {
        var shortChain = new TokenOptions { AbsoluteLifetime = TimeSpan.FromMinutes(20) };
        var longChain = new TokenOptions { AbsoluteLifetime = TimeSpan.FromMinutes(60) };
        DateTimeOffset chainDeadline = Start + TimeSpan.FromMinutes(20);
        (string first, _) = await _rig.IssueCachedAsync(_rig.Service(shortChain));

        _rig.At(TimeSpan.FromMinutes(10));
        RedeemedContinuation one = (await _rig.RedeemAsync(_rig.Service(shortChain), first)).ShouldBeRedeemed();
        string inline = (await _rig.Service(shortChain).IssueSuccessorAsync(one, Snapshot(64), Ct)).ShouldBeIssued();
        IsReference(inline).ShouldBeFalse();

        _rig.At(TimeSpan.FromMinutes(14));
        RedeemedContinuation two = (await _rig.RedeemAsync(_rig.Service(longChain), inline)).ShouldBeRedeemed();
        string cached = (await _rig.Service(longChain).IssueSuccessorAsync(two, Snapshot(CachedSnapshotLength, 2), Ct)).ShouldBeIssued();
        IsReference(cached).ShouldBeTrue();
        _rig.Store.ExpiryOf(_rig.Store.Written[^1]).ShouldBe(chainDeadline);

        _rig.Clock.Now = chainDeadline - TimeSpan.FromTicks(1);
        RedeemedContinuation three = (await _rig.RedeemAsync(_rig.Service(longChain), cached)).ShouldBeRedeemed();
        RedeemedContinuation inlineAgain = (await _rig.RedeemAsync(_rig.Service(longChain), inline)).ShouldBeRedeemed();

        _rig.Clock.Now = chainDeadline;
        (await _rig.RedeemAsync(_rig.Service(longChain), cached)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        int sets = _rig.Store.Sets;
        (await _rig.Service(longChain).IssueSuccessorAsync(three, Snapshot(CachedSnapshotLength, 3), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        (await _rig.Service(longChain).IssueSuccessorAsync(inlineAgain, Snapshot(CachedSnapshotLength, 3), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        _rig.Store.Sets.ShouldBe(sets);
    }

    private async Task<(string Reference, string Key, RedeemedContinuation Redeemed)> RedeemCachedAt(TimeSpan redeemAt)
    {
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        _rig.At(redeemAt);
        return (reference, key, (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRedeemed());
    }
}

using System.Buffers.Text;
using Ignixa.Federation.Api.Continuations;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.CachedContinuationRig;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;
using Operation = Ignixa.Federation.Tests.Integration.Continuations.ContractContinuationCache.Operation;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Redeeming cached continuation references against the synchronized contract store fixture: what is checked before
/// the store is consulted, how the stored value is authenticated against its reference, and how store failures map.
/// </summary>
public sealed class CachedContinuationRedemptionTests : IDisposable
{
    private static readonly TimeSpan Sliding = TimeSpan.FromMinutes(15);
    private readonly CachedContinuationRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task RepeatedRedemptionOnAnyInstance_ReturnsTheSameSnapshot_AndNeverRefreshesOrWrites()
    {
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        ContinuationTokenService first = _rig.Service();
        ContinuationTokenService second = _rig.Service();

        _rig.At(TimeSpan.FromMinutes(14));
        foreach (ContinuationTokenService instance in new[] { first, second, first, second })
        {
            (await _rig.RedeemAsync(instance, reference)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(CachedSnapshotLength));
        }

        _rig.Store.ExpiryOf(key).ShouldBe(Start + Sliding);
        (_rig.Store.Gets, _rig.Store.Sets, _rig.Store.Shortens).ShouldBe((4, 1, 0));
        _rig.Revisions.Consulted.Distinct().Count().ShouldBe(3);
    }

    [Theory]
    [InlineData("r1.")]
    [InlineData("r1.=")]
    [InlineData("r1.AAAA=")]
    [InlineData("r1.AA AA")]
    [InlineData("r1.A")]
    [InlineData("r1.AB")]
    [InlineData("r1.+/+/")]
    [InlineData("r1.AAAA.AAAA")]
    public async Task MalformedReference_IsInvalid_WithoutUnprotectingOrReadingTheStore(string reference)
    {
        var counting = new CountingDataProtectionProvider(_rig.Ring.CreateProvider());

        (await _rig.RedeemAsync(_rig.Service(provider: counting), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);

        counting.UnprotectCalls.ShouldBe(0);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Fact]
    public async Task ReferenceLength_IsBoundedBeforeUnprotect()
    {
        var counting = new CountingDataProtectionProvider(_rig.Ring.CreateProvider());
        ContinuationTokenService service = _rig.Service(provider: counting);
        string atLimit = ContinuationTokenService.ReferencePrefix + new string('A', ContinuationTokenService.MaxReferenceLength - 3);

        (await _rig.RedeemAsync(service, atLimit + "A")).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counting.UnprotectCalls.ShouldBe(0);

        (await _rig.RedeemAsync(service, atLimit)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counting.UnprotectCalls.ShouldBe(1);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Fact]
    public async Task TamperedReference_IsInvalid_WithoutReadingTheStore()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        char[] chars = reference.ToCharArray();
        int middle = chars.Length / 2;
        chars[middle] = chars[middle] == 'A' ? 'B' : 'A';

        (await _rig.RedeemAsync(_rig.Service(), new string(chars))).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public async Task AuthenticReferenceWithTheWrongFrame_IsInvalid_WithoutReadingTheStore(int handleLength)
    {
        IDataProtectionProvider provider = _rig.Ring.CreateProvider();
        long now = Start.UtcTicks;
        string reference = Reference(provider, Frame(now, now, now + Sliding.Ticks, now + Sliding.Ticks, Caller(), Empty(), new byte[handleLength]));

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Fact]
    public async Task OtherCallerOrAWindowMiss_IsDecidedFromTheReference_WithoutReadingTheStore()
    {
        _rig.At(TimeSpan.FromMinutes(5));
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        ContinuationTokenService service = _rig.Service();

        (await _rig.RedeemAsync(service, reference, Caller("Reader"))).ShouldBeRejected(ContinuationRedemptionOutcome.CallerMismatch);
        _rig.At(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        (await _rig.RedeemAsync(service, reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        _rig.At(TimeSpan.FromMinutes(5) + Sliding);
        (await _rig.RedeemAsync(service, reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);

        _rig.Store.Gets.ShouldBe(0);
        _rig.Revisions.Consulted.ShouldBeEmpty();

        _rig.At(TimeSpan.FromMinutes(5) + Sliding - TimeSpan.FromTicks(1));
        (await _rig.RedeemAsync(service, reference)).ShouldBeRedeemed();
    }

    [Fact]
    public async Task OtherApplicationDiscriminator_IsInvalid_WithoutReadingTheStore()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());

        (await _rig.RedeemAsync(_rig.Service(provider: _rig.Ring.CreateProvider("Other.Application")), reference))
            .ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Fact]
    public async Task PayloadsPresentedUnderAnotherFormat_AreInvalid()
    {
        ContinuationTokenService service = _rig.Service();
        string inline = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(10), Ct)).ShouldBeIssued();
        (string reference, string key) = await _rig.IssueCachedAsync(service);
        string body = reference[ContinuationTokenService.ReferencePrefix.Length..];

        (await _rig.RedeemAsync(service, ContinuationTokenService.ReferencePrefix + inline)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        (await _rig.RedeemAsync(service, body)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        (await _rig.RedeemAsync(service, Base64Url.EncodeToString(_rig.Store.ValueOf(key)))).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        _rig.Store.Gets.ShouldBe(0);
    }

    [Fact]
    public async Task ValueOfAnotherHandle_IsInvalid()
    {
        ContinuationTokenService service = _rig.Service();
        (string reference, string key) = await _rig.IssueCachedAsync(service, seed: 1);
        (_, string otherKey) = await _rig.IssueCachedAsync(service, seed: 2);
        _rig.Store.Replace(key, _rig.Store.ValueOf(otherKey));

        (await _rig.RedeemAsync(service, reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
    }

    [Theory]
    [InlineData("agrees")]
    [InlineData("chain-issued")]
    [InlineData("issued")]
    [InlineData("chain-deadline")]
    [InlineData("deadline")]
    [InlineData("binding")]
    public async Task AuthenticValueThatDisagreesWithItsReference_IsInvalid(string field)
    {
        IDataProtectionProvider provider = _rig.Ring.CreateProvider();
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        long t = Start.UtcTicks;
        (long chainIssued, long issued, long chainDeadline, long sliding) = (t, t, t + TimeSpan.FromMinutes(60).Ticks, t + Sliding.Ticks);
        ContinuationBinding binding = Caller();
        switch (field)
        {
            case "chain-issued": chainIssued--; break;
            case "issued": issued++; break;
            case "chain-deadline": chainDeadline--; break;
            case "deadline": sliding--; break;
            case "binding": binding = Caller("Reader"); break;
        }

        byte[] value = Frame(chainIssued, issued, chainDeadline, sliding, binding, Dependencies(), Snapshot(CachedSnapshotLength, 99));
        _rig.Store.Replace(key, ProtectValue(provider, HandleOf(provider, reference), value));

        ContinuationRedemption redemption = await _rig.RedeemAsync(_rig.Service(), reference);
        if (field == "agrees")
        {
            redemption.ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(CachedSnapshotLength, 99));
        }
        else
        {
            redemption.ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
            _rig.Revisions.Consulted.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task AuthenticValueOverTheCachedPlaintextLimit_IsInvalid()
    {
        const int Limit = 4096;
        IDataProtectionProvider provider = _rig.Ring.CreateProvider();
        ContinuationTokenService service = _rig.Service(maxCachedPlaintextBytes: Limit);
        (string reference, string key) = await _rig.IssueCachedAsync(service);
        long t = Start.UtcTicks;
        byte[] value = Frame(t, t, t + TimeSpan.FromMinutes(60).Ticks, t + Sliding.Ticks, Caller(), Dependencies(), Snapshot(Limit - Envelope + 1));
        _rig.Store.Replace(key, ProtectValue(provider, HandleOf(provider, reference), value));

        (await _rig.RedeemAsync(service, reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task StoredValueOverTheCiphertextBound_IsInvalid_WithoutUnprotectingIt()
    {
        var counting = new CountingDataProtectionProvider(_rig.Ring.CreateProvider());
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        _rig.Store.Replace(key, new byte[ContinuationTokenService.DefaultMaxCachedPlaintextBytes + ContinuationProtector.ProtectionOverheadAllowance + 1]);

        (await _rig.RedeemAsync(_rig.Service(provider: counting), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
        counting.UnprotectCalls.ShouldBe(1);
    }

    [Fact]
    public async Task CorruptStoredValue_IsInvalid()
    {
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        byte[] value = _rig.Store.ValueOf(key);
        value[^1] ^= 0x01;
        _rig.Store.Replace(key, value);

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Invalid);
    }

    [Fact]
    public async Task MissingEntry_IsGone()
    {
        (string reference, string key) = await _rig.IssueCachedAsync(_rig.Service());
        _rig.Store.Evict(key);

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Fact]
    public async Task EntryExpiredByTheStoreClockAhead_IsGone()
    {
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, _) = await rig.IssueCachedAsync(rig.Service());

        backend.Now = Start + Sliding;
        rig.At(TimeSpan.FromMinutes(1));

        (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableReadOrLostReadResponse_IsStoreUnavailable(bool lostResponse)
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        if (lostResponse)
        {
            _rig.Store.AfterApply = _ => throw new ContinuationCacheUnavailableException();
        }
        else
        {
            _rig.Store.BeforeApply = (_, _) => throw new ContinuationCacheUnavailableException();
        }

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.StoreUnavailable);
        _rig.Revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ValueOverTheProviderMaximum_IsReportedUnavailable_NotMaterialized()
    {
        using var rig = new CachedContinuationRig(maxValueBytes: ContinuationTokenService.DefaultMaxCachedPlaintextBytes + ContinuationProtector.ProtectionOverheadAllowance);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        rig.Store.Replace(key, new byte[rig.Store.MaxValueBytes + 1]);

        (await rig.RedeemAsync(rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.StoreUnavailable);
    }

    [Fact]
    public async Task ReadThatOutlivesTheStoreTimeout_IsStoreUnavailable()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        Task<ContinuationRedemption> redeeming = _rig.RedeemAsync(_rig.Service(), reference);
        await entered.Task;
        _rig.At(ContinuationTokenService.StoreTimeout);

        (await redeeming).ShouldBeRejected(ContinuationRedemptionOutcome.StoreUnavailable);
    }

    [Fact]
    public async Task ReadThatIgnoresItsTokenAndSucceedsAfterTheStoreTimeout_IsStoreUnavailable()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationRedemption> redeeming = _rig.RedeemAsync(_rig.Service(), reference);
        await entered.Task;
        _rig.At(ContinuationTokenService.StoreTimeout);
        release.SetResult();

        (await redeeming).ShouldBeRejected(ContinuationRedemptionOutcome.StoreUnavailable);
        _rig.Revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadThatIgnoresItsTokenAndSucceedsAfterCallerCancellation_Propagates()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        using var caller = new CancellationTokenSource();
        _rig.Store.BeforeApply = (_, _) => caller.CancelAsync();

        await _rig.Service().RedeemAsync(reference, Caller(), _rig.Revisions.Lookup, caller.Token).ShouldBeCanceledAsync();
        _rig.Revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public async Task CallerCancellationDuringTheRead_Propagates()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        using var caller = new CancellationTokenSource();
        _rig.Store.BeforeApply = async (_, token) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        };

        await _rig.Service().RedeemAsync(reference, Caller(), _rig.Revisions.Lookup, caller.Token).ShouldBeCanceledAsync();
    }

    [Fact]
    public async Task AContractViolatingReadFailure_IsNotTranslated()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        _rig.Store.BeforeApply = (_, _) => throw new InvalidOperationException("provider bug");

        await Should.ThrowAsync<InvalidOperationException>(() => _rig.RedeemAsync(_rig.Service(), reference));
    }

    [Fact]
    public async Task ChangedDependency_IsGone_AndOnlyItsOwnDependenciesAreConsulted()
    {
        (string reference, _) = await _rig.IssueCachedAsync(_rig.Service());
        _rig.Revisions.Set(ContinuationDependencyGroup.Shard, ShardB, "rev-b2");

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        _rig.Revisions.Consulted.ShouldAllBe(c =>
            (c.Group == ContinuationDependencyGroup.Shard && (c.Key == ShardA || c.Key == ShardB)) ||
            (c.Group == ContinuationDependencyGroup.Definition && c.Key == CodeDefinition));
    }

    [Fact]
    public async Task DeadlinePassingWhileTheReadIsInFlight_IsGone_EvenWhenTheStoreStillHoldsTheEntry()
    {
        // The store's clock lags, so the entry is still live there: only the redeemer's own window check refuses it.
        // The deadline passes within the store timeout, so it is the window and not the timeout that refuses it.
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        (string reference, string key) = await rig.IssueCachedAsync(rig.Service());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.BeforeApply = async (operation, _) =>
        {
            operation.ShouldBe(Operation.Get);
            entered.SetResult();
            await release.Task;
        };

        rig.At(Sliding - TimeSpan.FromSeconds(2));
        Task<ContinuationRedemption> redeeming = rig.RedeemAsync(rig.Service(), reference);
        await entered.Task;
        rig.At(Sliding);
        release.SetResult();

        (await redeeming).ShouldBeRejected(ContinuationRedemptionOutcome.Gone);
        rig.Store.Gets.ShouldBe(1);
        rig.Store.Contains(key).ShouldBeTrue();
        rig.Revisions.Consulted.ShouldBeEmpty();
    }

    [Fact]
    public void Refusals_RenderNothingFromTheToken() =>
        ContinuationRedemption.StoreUnavailable.ToString().ShouldBe(typeof(ContinuationRedemption).FullName);

    private static ContinuationDependencySet Empty() => ContinuationDependencySet.Create([], []);

    private static byte[] Frame(
        long chainIssued, long issued, long chainDeadline, long sliding, ContinuationBinding binding, ContinuationDependencySet dependencies, byte[] snapshot)
    {
        byte[] plaintext = new byte[ContinuationEnvelope.Measure(dependencies, snapshot.Length)];
        ContinuationEnvelope.Write(plaintext, chainIssued, issued, chainDeadline, sliding, binding.Hash, dependencies, snapshot);
        return plaintext;
    }

    private static string Reference(IDataProtectionProvider provider, byte[] handle) =>
        ContinuationTokenService.ReferencePrefix + Base64Url.EncodeToString(provider.CreateProtector(ContinuationTokenService.ReferencePurpose).Protect(handle));

    // The documented layout: the reference's plaintext is an envelope with no dependencies whose snapshot is the handle id.
    private static byte[] HandleOf(IDataProtectionProvider provider, string reference)
    {
        byte[] handle = provider.CreateProtector(ContinuationTokenService.ReferencePurpose)
            .Unprotect(Base64Url.DecodeFromChars(reference.AsSpan(ContinuationTokenService.ReferencePrefix.Length)));
        ContinuationEnvelope.TryRead(handle, out ContinuationEnvelope envelope).ShouldBeTrue();
        return envelope.Snapshot.ToArray();
    }

    // The documented value protection: the value purpose, then the handle id in unpadded Base64Url as a subpurpose.
    private static byte[] ProtectValue(IDataProtectionProvider provider, byte[] handle, byte[] value) =>
        provider.CreateProtector(ContinuationTokenService.ValuePurpose, Base64Url.EncodeToString(handle)).Protect(value);
}

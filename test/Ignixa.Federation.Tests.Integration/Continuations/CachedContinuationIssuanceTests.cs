using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Tests.Integration.Continuations.CachedContinuationRig;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Issuing cached continuation references (design spec §7.4) against the synchronized contract store fixture. The
/// fixture is evidence about this component's use of <see cref="IContinuationCache"/>, not about a deployed provider.
/// </summary>
public sealed class CachedContinuationIssuanceTests : IDisposable
{
    private readonly CachedContinuationRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task ExactInlineLimit_StaysInlineWithoutTheStore_OneByteMore_IsACachedReference()
    {
        ContinuationTokenService service = _rig.Service();
        int fits = new TokenOptions().MaxInlineTokenBytes - Envelope;

        string inline = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(fits), Ct)).ShouldBeIssued();
        IsReference(inline).ShouldBeFalse();
        _rig.Store.Sets.ShouldBe(0);

        string reference = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(fits + 1), Ct)).ShouldBeIssued();
        IsReference(reference).ShouldBeTrue();
        reference.Length.ShouldBeLessThanOrEqualTo(ContinuationTokenService.MaxReferenceLength);
        _rig.Store.Sets.ShouldBe(1);

        (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(fits + 1));
        (await _rig.RedeemAsync(_rig.Service(), inline)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(fits));
        _rig.Store.Gets.ShouldBe(1);
    }

    [Theory]
    [InlineData(ContinuationTokenService.DefaultMaxCachedPlaintextBytes)]
    [InlineData(8192)]
    public async Task CachedLimit_ExactFits_OneMoreIsTooLarge_WithoutProtectingOrWriting(int maxCachedPlaintextBytes)
    {
        var counting = new CountingDataProtectionProvider(_rig.Ring.CreateProvider());
        ContinuationTokenService service = _rig.Service(provider: counting, maxCachedPlaintextBytes: maxCachedPlaintextBytes);
        int fits = maxCachedPlaintextBytes - Envelope;

        (await service.IssueAsync(Caller(), Dependencies(), Snapshot(fits + 1), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.TooLarge);
        counting.ProtectCalls.ShouldBe(0);
        _rig.Store.Sets.ShouldBe(0);

        string reference = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(fits), Ct)).ShouldBeIssued();
        _rig.Store.ValueOf(_rig.Store.Written[^1]).Length.ShouldBeLessThanOrEqualTo(maxCachedPlaintextBytes + ContinuationProtector.ProtectionOverheadAllowance);
        (await _rig.RedeemAsync(_rig.Service(maxCachedPlaintextBytes: maxCachedPlaintextBytes), reference)).ShouldBeRedeemed().Snapshot.ShouldBe(Snapshot(fits));
    }

    [Fact]
    public async Task CachedEntry_IsTheWholeEnvelope_WithAnAbsoluteExpiryAtTheTokenDeadline_UnderAKeyNotDerivedFromTheReference()
    {
        ContinuationTokenService service = _rig.Service();
        _rig.At(TimeSpan.FromMinutes(2));

        (string reference, string key) = await _rig.IssueCachedAsync(service);

        _rig.Store.ExpiryOf(key).ShouldBe(Start + TimeSpan.FromMinutes(17));
        key.ShouldStartWith(ContinuationTokenService.CacheKeyPrefix);
        reference.ShouldNotContain(key[ContinuationTokenService.CacheKeyPrefix.Length..]);
        _rig.Store.ValueOf(key).Length.ShouldBeGreaterThan(CachedSnapshotLength);

        // A second issue of the same snapshot is a different handle: nothing is shared or overwritten.
        (string other, string otherKey) = await _rig.IssueCachedAsync(service);
        other.ShouldNotBe(reference);
        otherKey.ShouldNotBe(key);
    }

    [Fact]
    public async Task TypedUnsortedSnapshotOverTheInlineLimit_RoundTripsThroughAnotherInstance()
    {
        string[] ids = [.. Enumerable.Range(0, 16).Select(i => $"shard-{i:D2}")];
        ShardRegistry registry = ShardRegistry.Create([.. ids.Select(Shard)], [new BackendProfile("Synthetic", true, 10, NextLinkRedemption.RepostToSearch, null)]);
        ResourceType observation = ResourceType.Parse("Observation");
        ParameterOccurrence[] parameters =
            [new("code", "http://loinc.org|8867-4,http://loinc.org|8480-6,http://loinc.org|8462-4"), new("date", "ge2026-01-01"), new("status", "final,amended")];
        ShardSearchRequest[] streams = [.. ids.Select(id => new ShardSearchRequest(ShardId.Parse(id), observation, parameters, 50, ShardSearchMethod.PostSearch))];
        var original = new UnsortedContinuationSnapshot(streams, 50, UnsortedContinuation.AtStart(streams[3].Shard));
        var codec = new UnsortedSnapshotCodec(ContinuationTokenService.DefaultMaxCachedPlaintextBytes, registry);
        codec.Encode(original, out byte[]? bytes).ShouldBe(UnsortedSnapshotOutcome.Success);
        bytes.ShouldNotBeNull().Length.ShouldBeGreaterThan(new TokenOptions().MaxInlineTokenBytes);

        string reference = (await _rig.Service().IssueAsync(Caller(), Dependencies(), bytes, Ct)).ShouldBeIssued();
        RedeemedContinuation redeemed = (await _rig.RedeemAsync(_rig.Service(), reference)).ShouldBeRedeemed();

        codec.Decode(redeemed.Snapshot.AsSpan(), out UnsortedContinuationSnapshot? restored).ShouldBe(UnsortedSnapshotOutcome.Success);
        restored.ShouldNotBeNull().Streams.Select(s => s.Shard).ShouldBe(streams.Select(s => s.Shard));
        restored.Streams.ShouldAllBe(s => s.Parameters.SequenceEqual(parameters));
        restored.Continuation.Shard.ShouldBe(streams[3].Shard);
    }

    [Fact]
    public async Task InlineIssueAndRedemption_NeverTouchTheStore()
    {
        _rig.Store.BeforeApply = (_, _) => throw new ContinuationCacheUnavailableException();
        ContinuationTokenService service = _rig.Service();

        string inline = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(100), Ct)).ShouldBeIssued();
        RedeemedContinuation redeemed = (await _rig.RedeemAsync(service, inline)).ShouldBeRedeemed();
        (await service.IssueSuccessorAsync(redeemed, Snapshot(100, 9), Ct)).ShouldBeIssued();

        (_rig.Store.Gets, _rig.Store.Sets, _rig.Store.Shortens).ShouldBe((0, 0, 0));
    }

    [Fact]
    public void Construction_RequiresACachedLimitBetweenTheInlineLimitAndAStoreThatHoldsItsCiphertext()
    {
        int inlineLimit = new TokenOptions().MaxInlineTokenBytes;
        Should.Throw<ArgumentOutOfRangeException>(() => _rig.Service(maxCachedPlaintextBytes: inlineLimit - 1));
        Should.Throw<ArgumentOutOfRangeException>(() => _rig.Service(maxCachedPlaintextBytes: Array.MaxLength));

        const int Cached = 8192;
        using var small = new CachedContinuationRig(maxValueBytes: Cached + ContinuationProtector.ProtectionOverheadAllowance - 1);
        Should.Throw<ArgumentOutOfRangeException>(() => small.Service(maxCachedPlaintextBytes: Cached));

        using var exact = new CachedContinuationRig(maxValueBytes: Cached + ContinuationProtector.ProtectionOverheadAllowance);
        exact.Service(maxCachedPlaintextBytes: Cached).ShouldNotBeNull();
        _rig.Service(maxCachedPlaintextBytes: inlineLimit).ShouldNotBeNull();
    }

    [Fact]
    public async Task UnavailableWrite_IsStoreUnavailable_WithNoToken()
    {
        _rig.Store.BeforeApply = (_, _) => throw new ContinuationCacheUnavailableException();

        (await _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct))
            .ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteWhoseResponseIsLost_IsStoreUnavailable_AndTheUnreferencedEntryExpiresWithItsDeadline()
    {
        _rig.Store.AfterApply = _ => throw new ContinuationCacheUnavailableException();

        (await _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct))
            .ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.ExpiryOf(_rig.Store.Written.ShouldHaveSingleItem()).ShouldBe(Start + new TokenOptions().SlidingLifetime);
    }

    [Fact]
    public async Task WriteThatOutlivesTheStoreTimeout_IsStoreUnavailable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct);
        await entered.Task;
        _rig.At(ContinuationTokenService.StoreTimeout - TimeSpan.FromTicks(1));
        issuing.IsCompleted.ShouldBeFalse();
        _rig.At(ContinuationTokenService.StoreTimeout);

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
    }

    [Fact]
    public async Task CallerCancellationDuringTheWrite_Propagates()
    {
        using var caller = new CancellationTokenSource();
        _rig.Store.BeforeApply = async (_, token) =>
        {
            await caller.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        };

        await _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), caller.Token).ShouldBeCanceledAsync();
    }

    [Fact]
    public async Task WriteThatIgnoresItsTokenAndSucceedsAfterTheStoreTimeout_IsStoreUnavailable()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationIssuance> issuing = _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct);
        await entered.Task;
        _rig.At(ContinuationTokenService.StoreTimeout);
        release.SetResult();

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.StoreUnavailable);
        _rig.Store.ExpiryOf(_rig.Store.Written.ShouldHaveSingleItem()).ShouldBe(Start + new TokenOptions().SlidingLifetime);
    }

    [Fact]
    public async Task WriteThatIgnoresItsTokenAndSucceedsAfterCallerCancellation_Propagates()
    {
        using var caller = new CancellationTokenSource();
        _rig.Store.BeforeApply = (_, _) => caller.CancelAsync();

        await _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), caller.Token).ShouldBeCanceledAsync();
        _rig.Store.Written.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task WriteAlreadyExpiredByTheStoreClock_IsGone_AndNothingIsStored()
    {
        // The store's clock runs ahead and passes the entry's deadline while the write is held: it refuses the write
        // at its linearization point, which is a normal refusal, not a store failure.
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationIssuance> issuing = rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct);
        await entered.Task;
        backend.Now = Start + new TokenOptions().SlidingLifetime;
        release.SetResult();

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        rig.Store.Sets.ShouldBe(1);
        rig.Store.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteConfirmedOnlyAfterTheTokenDeadline_IsGone()
    {
        // The store's clock lags, so it keeps the late entry; the token's authenticated deadline has still passed.
        // The lifetime is shorter than the store timeout, so it is the deadline and not the timeout that refuses it.
        var backend = new SettableClock(Start);
        using var rig = new CachedContinuationRig(backend);
        var shortLived = new TokenOptions { SlidingLifetime = TimeSpan.FromSeconds(2) };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.BeforeApply = async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<ContinuationIssuance> issuing = rig.Service(shortLived).IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct);
        await entered.Task;
        rig.At(shortLived.SlidingLifetime);
        release.SetResult();

        (await issuing).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        rig.Store.ExpiryOf(rig.Store.Written.ShouldHaveSingleItem()).ShouldBe(Start + shortLived.SlidingLifetime);
    }

    [Fact]
    public async Task AContractViolatingStoreFailure_IsNotTranslated()
    {
        _rig.Store.BeforeApply = (_, _) => throw new InvalidOperationException("provider bug");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct));
    }

    [Fact]
    public async Task AtTheEndOfRepresentableTime_NothingExpiredIsWritten()
    {
        _rig.Clock.Now = DateTimeOffset.MaxValue;

        (await _rig.Service().IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength), Ct)).ShouldBeRefused(ContinuationIssuanceOutcome.Gone);
        _rig.Store.Sets.ShouldBe(0);
    }

    [Fact]
    public void Outcomes_RenderNoTokenOrSnapshot()
    {
        ContinuationIssuance.TooLarge.ToString().ShouldBe(typeof(ContinuationIssuance).FullName);
        ContinuationIssuance.Issued("r1.secret", Start.UtcTicks).ToString().ShouldBe(typeof(ContinuationIssuance).FullName);
        new ContinuationCacheUnavailableException().InnerException.ShouldBeNull();
    }

    private static ShardOptions Shard(string id) => new()
    {
        Id = id,
        Alias = id,
        BaseUrl = $"https://{id}.example.test/fhir",
        Profile = "Synthetic",
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.example.test" },
    };
}

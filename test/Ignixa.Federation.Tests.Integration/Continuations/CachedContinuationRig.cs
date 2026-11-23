using Ignixa.Federation.Api.Continuations;
using Ignixa.Federation.Core.Configuration;
using Microsoft.AspNetCore.DataProtection;
using static Ignixa.Federation.Tests.Integration.Continuations.ContinuationTestKit;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// One deployment for cached continuation tests: a shared isolated key ring, a manual clock, the shared contract store
/// fixture (optionally on its own backend clock), and the trusted revisions. Each <see cref="Service"/> call is an
/// independent instance.
/// </summary>
internal sealed class CachedContinuationRig : IDisposable
{
    /// <summary>A snapshot that never fits inline at the default limit, yet fits the smallest cached limit used here.</summary>
    public const int CachedSnapshotLength = 3000;

    public CachedContinuationRig(TimeProvider? backendClock = null, int maxValueBytes = 1 << 20) =>
        Store = new ContractContinuationCache(backendClock ?? Clock, maxValueBytes);

    public IsolatedKeyRing Ring { get; } = new();

    public SettableClock Clock { get; } = new(Start);

    public ContractContinuationCache Store { get; }

    public RevisionCatalog Revisions { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The envelope bytes around a snapshot for the test kit's dependencies.</summary>
    public static int Envelope => (int)ContinuationEnvelope.Measure(Dependencies(), 0);

    public ContinuationTokenService Service(
        TokenOptions? options = null,
        IDataProtectionProvider? provider = null,
        int maxCachedPlaintextBytes = ContinuationTokenService.DefaultMaxCachedPlaintextBytes) =>
        new(provider ?? Ring.CreateProvider(), options ?? new TokenOptions(), Clock, Store, maxCachedPlaintextBytes);

    public Task<ContinuationRedemption> RedeemAsync(ContinuationTokenService service, string? token, ContinuationBinding? caller = null) =>
        service.RedeemAsync(token, caller ?? Caller(), Revisions.Lookup, Ct);

    public async Task<(string Reference, string Key)> IssueCachedAsync(ContinuationTokenService service, byte seed = 7)
    {
        string reference = (await service.IssueAsync(Caller(), Dependencies(), Snapshot(CachedSnapshotLength, seed), Ct)).ShouldBeIssued();
        return (reference, Store.Written[^1]);
    }

    public void At(TimeSpan sinceStart) => Clock.Now = Start + sinceStart;

    public void Dispose() => Ring.Dispose();
}

using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Continuations;
using Microsoft.Extensions.Caching.Distributed;
using Shouldly;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// <see cref="RedisContinuationCache"/> against a real Redis process: the clock, expiry precision, atomic clamping,
/// the server-side read bound, and refusals. Opt-in through <see cref="RedisInteropFixture"/>; without it every test
/// here is reported as skipped, never as passed. Tests in one class run sequentially, so the server counters read by
/// the bound test see only this class's traffic.
/// </summary>
/// <remarks>
/// "Redis now" is read with <c>TIME</c> around each call. A script's clock is frozen at its start, between the
/// readings before and after it, so a reading taken before a call that is at or past an expiry proves the call ran at
/// or past it, and a reading taken after a call that is before an expiry proves the call ran before it.
/// </remarks>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous IDistributedCache members are under test.")]
public sealed class RedisContinuationCacheInteropTests(RedisInteropFixture fixture) : IClassFixture<RedisInteropFixture>
{
    private const int Limit = 64;

    private static readonly byte[] ValueA = [.. Enumerable.Range(0, Limit).Select(i => (byte)i)];
    private static readonly byte[] ValueB = [9, 8, 7];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Server_IsAVerifiedDisposableLoopbackRedis()
    {
        RedisInteropServer redis = fixture.Require();

        TestContext.Current.TestOutputHelper?.WriteLine($"Validated against Redis {redis.Version} (standalone primary).");
        Version.Parse(redis.Version).ShouldBeGreaterThanOrEqualTo(new Version(7, 0));
    }

    [Fact]
    public async Task SyncAndAsyncMembers_ShareOneKeySpace()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        DistributedCacheEntryOptions later = Until(await NowMsAsync(redis.First) + 60_000);

        await cache.SetAsync(key, ValueA, later, Ct);
        cache.Get(key).ShouldBe(ValueA);
        cache.Remove(key);
        (await cache.GetAsync(key, Ct)).ShouldBeNull();

        cache.Set(key, ValueB, later);
        (await cache.GetAsync(key, Ct)).ShouldBe(ValueB);
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
        await cache.RemoveAsync(key, Ct);
        cache.Get(key).ShouldBeNull();
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(30), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_DistinguishesAnEmptyValueFromAMiss()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();

        (await cache.GetAsync(key, Ct)).ShouldBeNull();
        await cache.SetAsync(key, [], Until(await NowMsAsync(redis.First) + 60_000), Ct);

        (await cache.GetAsync(key, Ct)).ShouldBe([]);
        cache.Get(key).ShouldBe([]);
    }

    [Fact]
    public async Task Get_ReturnsAValueAtTheLimitAndRefusesOneByteMore()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string atLimit = NewKey();
        string overLimit = NewKey();
        long later = await NowMsAsync(redis.First) + 60_000;
        byte[] tooLarge = new byte[Limit + 1];

        await cache.SetAsync(atLimit, ValueA, Until(later), Ct);
        await redis.First.ExecuteAsync("SET", (RedisKey)overLimit, tooLarge, "PXAT", later);

        (await cache.GetAsync(atLimit, Ct)).ShouldBe(ValueA);
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(overLimit, Ct));
        Should.Throw<ContinuationCacheUnavailableException>(() => cache.Get(overLimit));
        ((byte[]?)await redis.First.StringGetAsync(overLimit)).ShouldBe(tooLarge);
    }

    [Fact]
    public async Task Get_OverTheLimit_IsRefusedByTheServerWithoutSendingTheValue()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        const int Large = 1 << 20;
        await redis.First.ExecuteAsync("SET", (RedisKey)key, new byte[Large], "PXAT", await NowMsAsync(redis.First) + 60_000);

        long before = await NetOutputBytesAsync(redis.Inspector);
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(key, Ct));
        long after = await NetOutputBytesAsync(redis.Inspector);

        // Includes the first INFO reply and any client heartbeats; an order of magnitude below the stored value.
        (after - before).ShouldBeLessThan(64 * 1024);
        await redis.First.KeyDeleteAsync(key);
    }

    [Fact]
    public async Task OutOfPolicyEntries_AreUnavailableAndLeftUntouched()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string persistent = NewKey();
        string hash = NewKey();
        await redis.First.StringSetAsync(persistent, ValueB);
        await redis.First.HashSetAsync(hash, "field", "value");
        await redis.First.KeyExpireAsync(hash, TimeSpan.FromMinutes(5));
        long hashExpiry = await ExpiryMsAsync(redis.First, hash);

        foreach (string key in new[] { persistent, hash })
        {
            await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(key, Ct));
            Should.Throw<ContinuationCacheUnavailableException>(() => cache.Get(key));
            await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(1), Ct));
        }

        (await ExpiryMsAsync(redis.First, persistent)).ShouldBe(-1);
        (await ExpiryMsAsync(redis.First, hash)).ShouldBe(hashExpiry);
        ((byte[]?)await redis.First.StringGetAsync(persistent)).ShouldBe(ValueB);
        await redis.First.KeyDeleteAsync([persistent, hash]);
    }

    [Fact]
    public async Task Set_WritesTheAbsoluteExpiryTruncatedToTheMillisecond()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string aligned = NewKey();
        string fractional = NewKey();
        long deadline = await NowMsAsync(redis.First) + 60_000;

        await cache.SetAsync(aligned, ValueA, Until(deadline), Ct);
        cache.Set(fractional, ValueA, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.FromUnixTimeMilliseconds(deadline).AddTicks(TimeSpan.TicksPerMillisecond - 1),
        });

        (await ExpiryMsAsync(redis.First, aligned)).ShouldBe(deadline);
        (await ExpiryMsAsync(redis.First, fractional)).ShouldBe(deadline);
    }

    [Fact]
    public async Task Set_AtOrBeforeRedisNow_IsRefusedAndLeavesALiveEntryUntouched()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string live = NewKey();
        string absent = NewKey();
        long liveExpiry = await NowMsAsync(redis.First) + 60_000;
        await cache.SetAsync(live, ValueA, Until(liveExpiry), Ct);

        for (int round = 0; round < 20; round++)
        {
            long now = await NowMsAsync(redis.First);
            foreach (DateTimeOffset expired in new[]
            {
                DateTimeOffset.FromUnixTimeMilliseconds(now),
                DateTimeOffset.FromUnixTimeMilliseconds(now).AddTicks(TimeSpan.TicksPerMillisecond - 1),
                DateTimeOffset.FromUnixTimeMilliseconds(now - 1),
                DateTimeOffset.UnixEpoch,
            })
            {
                var options = new DistributedCacheEntryOptions { AbsoluteExpiration = expired };
                foreach (string key in new[] { live, absent })
                {
                    await Should.ThrowAsync<ContinuationCacheEntryExpiredException>(() => cache.SetAsync(key, ValueB, options, Ct));
                    Should.Throw<ContinuationCacheEntryExpiredException>(() => cache.Set(key, ValueB, options));
                }
            }
        }

        (await cache.GetAsync(live, Ct)).ShouldBe(ValueA);
        (await ExpiryMsAsync(redis.First, live)).ShouldBe(liveExpiry);
        (await redis.First.KeyExistsAsync(absent)).ShouldBeFalse();
    }

    [Fact]
    public async Task Entry_IsLiveBeforeItsExpiryMillisecondAndGoneFromIt()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        TimeSpan noOpLimit = TimeSpan.FromHours(1);
        int readsAtExpiry = 0;
        int shortensAtExpiry = 0;

        for (int attempt = 0; attempt < 50 && (readsAtExpiry < 3 || shortensAtExpiry < 3); attempt++)
        {
            string key = NewKey();
            long expiry = await NowMsAsync(redis.First) + 4;
            try
            {
                await cache.SetAsync(key, ValueA, Until(expiry), Ct);
            }
            catch (ContinuationCacheEntryExpiredException)
            {
                continue; // The write itself reached the server too late on a slow round; nothing was stored.
            }

            long before;
            do
            {
                before = await NowMsAsync(redis.First);
                byte[]? read = await cache.GetAsync(key, Ct);
                long after = await NowMsAsync(redis.First);
                Classify(before, after, expiry, live: read is not null, ref readsAtExpiry);

                before = await NowMsAsync(redis.First);
                bool shortened = await cache.TryShortenExpiryAsync(key, noOpLimit, Ct);
                after = await NowMsAsync(redis.First);
                Classify(before, after, expiry, live: shortened, ref shortensAtExpiry);
            }
            while (before <= expiry + 1);
        }

        readsAtExpiry.ShouldBeGreaterThanOrEqualTo(3, "The probe never read at the expiry millisecond; the boundary was not exercised.");
        shortensAtExpiry.ShouldBeGreaterThanOrEqualTo(3, "The probe never shortened at the expiry millisecond; the boundary was not exercised.");
    }

    [Fact]
    public async Task TryShorten_ClampsToTheEarlierOfTheEntryExpiryAndRedisNowPlusTheLimit()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        await cache.SetAsync(key, ValueA, Until(await NowMsAsync(redis.First) + 60_000), Ct);

        long before = await NowMsAsync(redis.First);
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        long after = await NowMsAsync(redis.First);
        long clamped = await ExpiryMsAsync(redis.First, key);
        clamped.ShouldBeInRange(before + 10_000, after + 10_000);

        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue("A live entry the limit does not shorten is not a miss.");
        (await ExpiryMsAsync(redis.First, key)).ShouldBe(clamped);

        before = await NowMsAsync(redis.First);
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromMilliseconds(5_000.9), Ct)).ShouldBeTrue();
        after = await NowMsAsync(redis.First);
        long earlier = await ExpiryMsAsync(redis.First, key);
        earlier.ShouldBeInRange(before + 5_000, after + 5_000);
        earlier.ShouldBeLessThan(clamped);

        (await cache.GetAsync(key, Ct)).ShouldBe(ValueA);
    }

    [Fact]
    public async Task TryShorten_WithASubMillisecondLimit_EndsTheEntry()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        await cache.SetAsync(key, ValueA, Until(await NowMsAsync(redis.First) + 60_000), Ct);

        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1), Ct)).ShouldBeTrue();

        (await cache.GetAsync(key, Ct)).ShouldBeNull();
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(1), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryShorten_OnAMissingKey_IsFalseAndCreatesNothing()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();

        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(10), Ct)).ShouldBeFalse();

        (await redis.First.KeyExistsAsync(key)).ShouldBeFalse();
    }

    [Fact]
    public async Task CompetingClampsFromDistinctInstances_KeepTheEarliestExpiry()
    {
        RedisInteropServer redis = fixture.Require();
        var first = new RedisContinuationCache(redis.First, Limit);
        var second = new RedisContinuationCache(redis.Second, Limit);

        for (int round = 0; round < 20; round++)
        {
            string key = NewKey();
            await first.SetAsync(key, ValueA, Until(await NowMsAsync(redis.First) + 60_000), Ct);
            (RedisContinuationCache shorter, RedisContinuationCache longer) = round % 2 == 0 ? (first, second) : (second, first);

            long before = await NowMsAsync(redis.First);
            bool[] results = await Task.WhenAll(
                longer.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(40), Ct),
                shorter.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(10), Ct),
                longer.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(20), Ct),
                shorter.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(15), Ct));
            long after = await NowMsAsync(redis.First);

            results.ShouldAllBe(live => live);
            long expiry = await ExpiryMsAsync(redis.First, key);
            expiry.ShouldBeInRange(before + 10_000, after + 10_000);

            (await longer.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(50), Ct)).ShouldBeTrue();
            (await ExpiryMsAsync(redis.First, key)).ShouldBe(expiry);
            (await shorter.GetAsync(key, Ct)).ShouldBe(ValueA);
        }
    }

    [Fact]
    public async Task PreCanceledCalls_ChangeNothing()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string live = NewKey();
        string absent = NewKey();
        long expiry = await NowMsAsync(redis.First) + 60_000;
        await cache.SetAsync(live, ValueA, Until(expiry), Ct);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => cache.SetAsync(absent, ValueB, Until(expiry), canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.SetAsync(live, ValueB, Until(expiry + 1), canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.TryShortenExpiryAsync(live, TimeSpan.FromSeconds(1), canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.RemoveAsync(live, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.GetAsync(live, canceled.Token));

        (await redis.First.KeyExistsAsync(absent)).ShouldBeFalse();
        (await ExpiryMsAsync(redis.First, live)).ShouldBe(expiry);
        (await cache.GetAsync(live, Ct)).ShouldBe(ValueA);
    }

    // A call whose preceding reading is at or past the expiry ran at or past it, so it must see no live entry; one
    // whose following reading is before the expiry ran before it, so it must see the entry.
    private static void Classify(long before, long after, long expiry, bool live, ref int atExpiry)
    {
        if (before >= expiry)
        {
            live.ShouldBeFalse($"A call at Redis millisecond {before - expiry} past the expiry saw a live entry.");
            if (before == expiry)
            {
                atExpiry++;
            }
        }
        else if (after < expiry)
        {
            live.ShouldBeTrue("A call that completed before the expiry did not see the live entry.");
        }
    }

    private static string NewKey() => ContinuationTokenService.CacheKeyPrefix + "interop:" + Guid.NewGuid().ToString("N");

    private static DistributedCacheEntryOptions Until(long unixMilliseconds) =>
        new() { AbsoluteExpiration = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) };

    private static async Task<long> NowMsAsync(IDatabase database)
    {
        var time = (RedisResult[])(await database.ExecuteAsync("TIME"))!;
        return ((long)time[0] * 1000) + ((long)time[1] / 1000);
    }

    private static async Task<long> ExpiryMsAsync(IDatabase database, string key) =>
        (long)await database.ExecuteAsync("PEXPIRETIME", (RedisKey)key);

    private static async Task<long> NetOutputBytesAsync(IDatabase database)
    {
        string info = (string)(await database.ExecuteAsync("INFO", "stats"))!;
        string line = info.Split('\n').Single(l => l.StartsWith("total_net_output_bytes:", StringComparison.Ordinal));
        return long.Parse(line["total_net_output_bytes:".Length..].TrimEnd('\r'), System.Globalization.CultureInfo.InvariantCulture);
    }
}

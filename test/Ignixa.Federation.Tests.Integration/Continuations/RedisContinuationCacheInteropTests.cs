using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Continuations;
using Microsoft.Extensions.Caching.Distributed;
using Shouldly;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// <see cref="RedisContinuationCache"/> against a real Redis process: the clock, expiry precision, atomic clamping,
/// the server-side size policy, reply shapes over RESP2 and RESP3, and refusals. Opt-in through
/// <see cref="RedisInteropFixture"/>; without it every test here is reported as skipped, never as passed. Tests in one
/// class run sequentially, so the server counters read by the bound test see only this class's traffic.
/// </summary>
/// <remarks>
/// Exact-millisecond behaviour is checked with <see cref="ScriptPrefixingDatabase"/>, which stages a key at the frozen
/// clock of the very execution that runs the adapter's script, rather than by sampling <c>TIME</c> around a call.
/// Failures never carry client details or stored contents: direct commands go through <see cref="RawAsync"/>, and
/// values are compared without printing them.
/// </remarks>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous IDistributedCache members are under test.")]
public sealed class RedisContinuationCacheInteropTests(RedisInteropFixture fixture) : IClassFixture<RedisInteropFixture>
{
    private const int Limit = 64;

    private static readonly byte[] ValueA = [.. Enumerable.Range(0, Limit).Select(i => (byte)i)];
    private static readonly byte[] ValueB = [9, 8, 7];
    private static readonly byte[] Staged = "staged"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Server_IsAVerifiedDisposableLoopbackRedis()
    {
        RedisInteropServer redis = fixture.Require();

        TestContext.Current.TestOutputHelper?.WriteLine($"Validated against Redis {redis.Version} (standalone primary, RESP2 and RESP3).");
        Version.Parse(redis.Version).ShouldBeGreaterThanOrEqualTo(new Version(7, 0));
    }

    [Fact]
    public async Task EveryMember_ConformsOverResp2AndResp3()
    {
        RedisInteropServer redis = fixture.Require();
        foreach (IDatabase database in new[] { redis.First, redis.Resp3 })
        {
            var cache = new RedisContinuationCache(database, Limit);
            string key = NewKey();
            DistributedCacheEntryOptions later = Until(await NowMsAsync(redis.First) + 60_000);

            ShouldBeMiss(await cache.GetAsync(key, Ct));
            ShouldBeMiss(cache.Get(key));
            (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(30), Ct)).ShouldBeFalse();
            await cache.RemoveAsync(key, Ct);
            cache.Remove(key);

            await cache.SetAsync(key, [], later, Ct);
            ShouldHold(await cache.GetAsync(key, Ct), []);
            ShouldHold(cache.Get(key), []);

            cache.Set(key, ValueA, later);
            ShouldHold(await cache.GetAsync(key, Ct), ValueA);
            (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(30), Ct)).ShouldBeTrue();
            await Should.ThrowAsync<ContinuationCacheEntryExpiredException>(() => cache.SetAsync(key, ValueB, Until(0), Ct));
            Should.Throw<ContinuationCacheEntryExpiredException>(() => cache.Set(key, ValueB, Until(0)));
            ShouldHold(cache.Get(key), ValueA);

            await cache.RemoveAsync(key, Ct);
            ShouldBeMiss(cache.Get(key));
            cache.Set(key, ValueB, later);
            cache.Remove(key);
            (await ExistsAsync(redis.First, key)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task ValuesOverTheLimit_AreUnavailableToReadAndShortenAndLeftUntouched()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string atLimit = NewKey();
        string overLimit = NewKey();
        long later = await NowMsAsync(redis.First) + 60_000;
        byte[] tooLarge = new byte[Limit + 1];

        await cache.SetAsync(atLimit, ValueA, Until(later), Ct);
        await RawAsync(redis.First, "SET", (RedisKey)overLimit, tooLarge, "PXAT", later);

        ShouldHold(await cache.GetAsync(atLimit, Ct), ValueA);
        (await cache.TryShortenExpiryAsync(atLimit, TimeSpan.FromSeconds(10), Ct)).ShouldBeTrue();
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(overLimit, Ct));
        Should.Throw<ContinuationCacheUnavailableException>(() => cache.Get(overLimit));
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.TryShortenExpiryAsync(overLimit, TimeSpan.FromSeconds(10), Ct));
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(
            () => cache.TryShortenExpiryAsync(overLimit, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1), Ct));

        (await ExpiryMsAsync(redis.First, overLimit)).ShouldBe(later);
        ShouldHold((byte[]?)await RawAsync(redis.First, "GET", (RedisKey)overLimit), tooLarge);
        await DeleteAsync(redis.First, atLimit, overLimit);
    }

    [Fact]
    public async Task Get_OverTheLimit_IsRefusedByTheServerWithoutSendingTheValue()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        const int Large = 1 << 20;
        await RawAsync(redis.First, "SET", (RedisKey)key, new byte[Large], "PXAT", await NowMsAsync(redis.First) + 60_000);

        long before = await NetOutputBytesAsync(redis.Inspector);
        await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(key, Ct));
        long after = await NetOutputBytesAsync(redis.Inspector);

        // Includes the first INFO reply and any client heartbeats; an order of magnitude below the stored value.
        (after - before).ShouldBeLessThan(64 * 1024);
        await DeleteAsync(redis.First, key);
    }

    [Fact]
    public async Task OutOfPolicyEntries_AreUnavailableAndLeftUntouched()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string persistent = NewKey();
        string hash = NewKey();
        await RawAsync(redis.First, "SET", (RedisKey)persistent, ValueB);
        await RawAsync(redis.First, "HSET", (RedisKey)hash, "field", "value");
        await RawAsync(redis.First, "PEXPIRE", (RedisKey)hash, 300_000);
        long hashExpiry = await ExpiryMsAsync(redis.First, hash);

        foreach (string key in new[] { persistent, hash })
        {
            await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(key, Ct));
            Should.Throw<ContinuationCacheUnavailableException>(() => cache.Get(key));
            await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(1), Ct));
        }

        (await ExpiryMsAsync(redis.First, persistent)).ShouldBe(-1);
        (await ExpiryMsAsync(redis.First, hash)).ShouldBe(hashExpiry);
        ShouldHold((byte[]?)await RawAsync(redis.First, "GET", (RedisKey)persistent), ValueB);
        await DeleteAsync(redis.First, persistent, hash);
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
        await DeleteAsync(redis.First, aligned, fractional);
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

        // Each expiry is at or before a TIME reading taken earlier, so it is at or before Redis now when written.
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

        ShouldHold(await cache.GetAsync(live, Ct), ValueA);
        (await ExpiryMsAsync(redis.First, live)).ShouldBe(liveExpiry);
        (await ExistsAsync(redis.First, absent)).ShouldBeFalse();
        await DeleteAsync(redis.First, live);
    }

    [Fact]
    public async Task StagingAtTheScriptsOwnMillisecond_LeavesTheKeyPresent()
    {
        // Guards the equality tests below against passing vacuously: native expiry alone does not hide a key whose
        // expiry is the execution's own millisecond, so only the adapter's guard can make it a miss.
        RedisInteropServer redis = fixture.Require();
        string key = NewKey();

        RedisResult reply = await ScriptAsync(redis.First, StageKey(offsetMs: 0) + """

            return { redis.call('EXISTS', KEYS[1]), redis.call('PEXPIRETIME', KEYS[1]) - stageNow }
            """, key);

        var observed = (RedisResult[])reply!;
        ((long)observed[0]).ShouldBe(1);
        ((long)observed[1]).ShouldBe(0);
        await DeleteAsync(redis.First, key);
    }

    [Fact]
    public async Task Get_AtTheEntrysExactExpiryMillisecond_IsAMissAndOneMillisecondEarlierIsLive()
    {
        RedisInteropServer redis = fixture.Require();
        (IDatabase database, ScriptPrefixingDatabase staging) = ScriptPrefixingDatabase.Over(redis.First);
        var cache = new RedisContinuationCache(database, Limit);

        for (int round = 0; round < 5; round++)
        {
            string key = NewKey();
            staging.Prefix = StageKey(offsetMs: 0);
            ShouldBeMiss(await cache.GetAsync(key, Ct));

            staging.Prefix = StageKey(offsetMs: 1);
            ShouldHold(await cache.GetAsync(key, Ct), Staged);
            await DeleteAsync(redis.First, key);
        }
    }

    [Fact]
    public async Task TryShorten_AtTheEntrysExactExpiryMillisecond_IsFalseAndOneMillisecondEarlierIsTrue()
    {
        RedisInteropServer redis = fixture.Require();
        (IDatabase database, ScriptPrefixingDatabase staging) = ScriptPrefixingDatabase.Over(redis.First);
        var cache = new RedisContinuationCache(database, Limit);

        for (int round = 0; round < 5; round++)
        {
            string key = NewKey();
            staging.Prefix = StageKey(offsetMs: 0);
            (await cache.TryShortenExpiryAsync(key, TimeSpan.FromHours(1), Ct)).ShouldBeFalse();

            staging.Prefix = StageKey(offsetMs: 1);
            (await cache.TryShortenExpiryAsync(key, TimeSpan.FromHours(1), Ct)).ShouldBeTrue();
            await DeleteAsync(redis.First, key);
        }
    }

    [Fact]
    public async Task Set_ExpiringAtRedisNowExactly_IsRefusedAndOneMillisecondLaterIsStored()
    {
        RedisInteropServer redis = fixture.Require();
        (IDatabase database, ScriptPrefixingDatabase staging) = ScriptPrefixingDatabase.Over(redis.First);
        var cache = new RedisContinuationCache(database, Limit);
        DistributedCacheEntryOptions ignored = Until(await NowMsAsync(redis.First) + 60_000);

        for (int round = 0; round < 5; round++)
        {
            string key = NewKey();
            staging.Prefix = StageWriteExpiry(offsetMs: 0);
            await Should.ThrowAsync<ContinuationCacheEntryExpiredException>(() => cache.SetAsync(key, ValueA, ignored, Ct));

            staging.Prefix = StageWriteExpiry(offsetMs: 1);
            await cache.SetAsync(key, ValueA, ignored, Ct);
            await DeleteAsync(redis.First, key);
        }
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

        ShouldHold(await cache.GetAsync(key, Ct), ValueA);
        await DeleteAsync(redis.First, key);
    }

    [Fact]
    public async Task TryShorten_WithASubMillisecondLimit_EndsTheEntry()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();
        await cache.SetAsync(key, ValueA, Until(await NowMsAsync(redis.First) + 60_000), Ct);

        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1), Ct)).ShouldBeTrue();

        ShouldBeMiss(await cache.GetAsync(key, Ct));
        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(1), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryShorten_OnAMissingKey_IsFalseAndCreatesNothing()
    {
        RedisInteropServer redis = fixture.Require();
        var cache = new RedisContinuationCache(redis.First, Limit);
        string key = NewKey();

        (await cache.TryShortenExpiryAsync(key, TimeSpan.FromSeconds(10), Ct)).ShouldBeFalse();

        (await ExistsAsync(redis.First, key)).ShouldBeFalse();
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
            ShouldHold(await shorter.GetAsync(key, Ct), ValueA);
            await DeleteAsync(redis.First, key);
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

        (await ExistsAsync(redis.First, absent)).ShouldBeFalse();
        (await ExpiryMsAsync(redis.First, live)).ShouldBe(expiry);
        ShouldHold(await cache.GetAsync(live, Ct), ValueA);
        await DeleteAsync(redis.First, live);
    }

    // Lua run before the adapter's script in the same execution: KEYS[1] holds Staged, expiring offsetMs after the
    // execution's own millisecond (floor(TIME), which equals Redis's frozen expiry clock there). It defines stageNow.
    private static string StageKey(int offsetMs) => $"""
        local stageTime = redis.call('TIME')
        local stageNow = tonumber(stageTime[1]) * 1000 + math.floor(tonumber(stageTime[2]) / 1000)
        redis.call('SET', KEYS[1], 'staged', 'PXAT', string.format('%.0f', stageNow + {offsetMs}))
        """;

    // Lua run before the adapter's write script in the same execution: it replaces the requested expiry, ARGV[2], with
    // offsetMs after the execution's own millisecond, shadowing ARGV for the rest of the script.
    private static string StageWriteExpiry(int offsetMs) => $$"""
        local stageTime = redis.call('TIME')
        local stageNow = tonumber(stageTime[1]) * 1000 + math.floor(tonumber(stageTime[2]) / 1000)
        local ARGV = { ARGV[1], string.format('%.0f', stageNow + {{offsetMs}}) }
        """;

    private static string NewKey() => ContinuationTokenService.CacheKeyPrefix + "interop:" + Guid.NewGuid().ToString("N");

    private static DistributedCacheEntryOptions Until(long unixMilliseconds) =>
        new() { AbsoluteExpiration = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) };

    private static void ShouldBeMiss(byte[]? actual) =>
        (actual is null).ShouldBeTrue("Expected a miss, but a value was returned; its contents are withheld.");

    private static void ShouldHold(byte[]? actual, byte[] expected) =>
        (actual is not null && actual.AsSpan().SequenceEqual(expected)).ShouldBeTrue("The value differs from the one expected; contents are withheld.");

    private static async Task<long> NowMsAsync(IDatabase database)
    {
        var time = (RedisResult[])(await RawAsync(database, "TIME"))!;
        return ((long)time[0] * 1000) + ((long)time[1] / 1000);
    }

    private static async Task<long> ExpiryMsAsync(IDatabase database, string key) =>
        (long)await RawAsync(database, "PEXPIRETIME", (RedisKey)key);

    private static async Task<bool> ExistsAsync(IDatabase database, string key) =>
        (long)await RawAsync(database, "EXISTS", (RedisKey)key) == 1;

    private static async Task DeleteAsync(IDatabase database, params string[] keys) =>
        await RawAsync(database, "DEL", [.. keys.Select(k => (object)(RedisKey)k)]);

    private static async Task<long> NetOutputBytesAsync(IDatabase inspector)
    {
        string info = (string)(await RawAsync(inspector, "INFO", "stats"))!;
        string line = info.Split('\n').Single(l => l.StartsWith("total_net_output_bytes:", StringComparison.Ordinal));
        return long.Parse(line["total_net_output_bytes:".Length..].TrimEnd('\r'), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<RedisResult> ScriptAsync(IDatabase database, string script, string key) =>
        Sanitized("EVAL", () => database.ScriptEvaluateAsync(script, [key]));

    // A direct command for setup or inspection. The client's failure messages can name the endpoint and the command's
    // arguments, so a failure is reported by command name only.
    private static Task<RedisResult> RawAsync(IDatabase database, string command, params object[] args) =>
        Sanitized(command, () => database.ExecuteAsync(command, args));

    private static async Task<RedisResult> Sanitized(string command, Func<Task<RedisResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException or RedisCommandException)
        {
            throw new InvalidOperationException($"A direct Redis {command} for test setup or inspection failed; client details are withheld.");
        }
    }
}

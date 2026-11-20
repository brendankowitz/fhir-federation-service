using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Ignixa.Federation.Api.Continuations;

/// <summary>
/// The native Redis <see cref="IContinuationCache"/> (design spec §7.4) over a caller-owned
/// <see cref="IDatabase"/>, which it borrows and never disposes. It requires Redis 7.0 or later (<c>PEXPIRETIME</c>),
/// which the host must verify before use: against an older server reads and shortens fail as unavailable, but writes
/// and removals can still succeed, so the adapter does not reject such a server by itself. Every member runs one
/// command on the primary, so each is atomic and linearizable per key on a standalone primary; see the provider
/// validation notes for replicated topologies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Clock and precision.</b> The backend clock is Redis's, read with <c>TIME</c> inside each script. An expiry
/// <c>E</c> is stored as the whole millisecond <c>D = floor(E)</c>, and an entry is live only while Redis now, in whole
/// milliseconds, is before <c>D</c>. So it is never readable at or after <c>E</c>; when <c>E</c> has a fraction of a
/// millisecond it ends up to that fraction early. Redis's own lazy expiry treats a key as expired only once
/// <c>now &gt; D</c>, which would expose it for the whole millisecond <c>D</c>; every script therefore checks
/// <c>D &lt;= now</c> itself rather than relying on native expiry. A write whose <c>D</c> is at or before Redis now
/// stores nothing and is refused as expired, including an expiry less than a millisecond away, for which no live Redis
/// millisecond exists.
/// </para>
/// <para>
/// <b>Policy.</b> Values are written only by <see cref="Set"/> and <see cref="SetAsync"/>, as strings with an absolute
/// expiry. A key holding anything else (no expiry, or not a string) is out of policy and reported as unavailable,
/// never as a miss, and is left untouched. So is a live value over <see cref="MaxValueBytes"/>: a read refuses it on
/// the server before it is sent, and a shorten refuses it without changing its expiry.
/// </para>
/// <para>
/// <b>Failures.</b> Client and server failures (<see cref="RedisException"/>, <see cref="RedisTimeoutException"/>,
/// <see cref="RedisCommandException"/>) and replies of any other wire type or value become a payload-free
/// <see cref="ContinuationCacheUnavailableException"/>; the client's messages name endpoints and keys. Anything else,
/// such as use after the caller disposed the connection, is a defect and propagates.
/// </para>
/// </remarks>
internal sealed class RedisContinuationCache : IContinuationCache
{
    /// <summary>The largest Redis string value (the default <c>proto-max-bulk-len</c>).</summary>
    internal const int MaxSupportedValueBytes = 512 * 1024 * 1024;

    private const CommandFlags OnPrimary = CommandFlags.DemandMaster;

    // Redis now in whole milliseconds, floor(TIME), which is never before the script's frozen expiry clock.
    private const string Now = """
        local time = redis.call('TIME')
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        """;

    // ARGV[1]: the maximum value length. Returns the value, or false for a miss.
    private const string ReadScript = $"""
        local expiry = redis.call('PEXPIRETIME', KEYS[1])
        if expiry == -2 then return false end
        if expiry == -1 then return redis.error_reply('ERR continuation entry out of policy') end
        {Now}
        if expiry <= now then return false end
        if redis.call('STRLEN', KEYS[1]) > tonumber(ARGV[1]) then
            return redis.error_reply('ERR continuation entry over the maximum')
        end
        return redis.call('GET', KEYS[1])
        """;

    // ARGV[1]: the value; ARGV[2]: the expiry in Unix milliseconds. Returns 1 when stored, 0 when already expired.
    private const string WriteScript = $"""
        {Now}
        if tonumber(ARGV[2]) <= now then return 0 end
        redis.call('SET', KEYS[1], ARGV[1], 'PXAT', ARGV[2])
        return 1
        """;

    // ARGV[1]: the limit in whole milliseconds; ARGV[2]: the maximum value length. Returns 1 when live (and now expiring
    // no later than the limit), 0 for no live entry. The same policy as a read: an entry the read script would refuse
    // (no expiry, not a string, or over the maximum) is refused here too, before anything changes. The target is
    // formatted as an integer because Redis may render a Lua number in exponent form.
    private const string ShortenScript = $"""
        local expiry = redis.call('PEXPIRETIME', KEYS[1])
        if expiry == -2 then return 0 end
        if expiry == -1 then return redis.error_reply('ERR continuation entry out of policy') end
        {Now}
        if expiry <= now then return 0 end
        if redis.call('STRLEN', KEYS[1]) > tonumber(ARGV[2]) then
            return redis.error_reply('ERR continuation entry over the maximum')
        end
        local target = now + tonumber(ARGV[1])
        if target < expiry then redis.call('PEXPIREAT', KEYS[1], string.format('%.0f', target)) end
        return 1
        """;

    private readonly IDatabase _database;

    /// <param name="database">The connection to the Redis primary; borrowed, not disposed.</param>
    /// <param name="maxValueBytes">The largest value stored and returned, from 1 to <see cref="MaxSupportedValueBytes"/>.</param>
    public RedisContinuationCache(IDatabase database, int maxValueBytes)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValueBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxValueBytes, MaxSupportedValueBytes);
        _database = database;
        MaxValueBytes = maxValueBytes;
    }

    public int MaxValueBytes { get; }

    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ToValue(Call(() => _database.ScriptEvaluate(ReadScript, [key], [MaxValueBytes], OnPrimary)));
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ToValue(await CallAsync(() => _database.ScriptEvaluateAsync(ReadScript, [key], [MaxValueBytes], OnPrimary), token)
            .ConfigureAwait(false));
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        long expiry = ExpiryOf(key, value, options);
        if (!ToConfirmation(Call(() => _database.ScriptEvaluate(WriteScript, [key], [value, expiry], OnPrimary))))
        {
            throw new ContinuationCacheEntryExpiredException();
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        long expiry = ExpiryOf(key, value, options);
        RedisResult reply = await CallAsync(() => _database.ScriptEvaluateAsync(WriteScript, [key], [value, expiry], OnPrimary), token)
            .ConfigureAwait(false);
        if (!ToConfirmation(reply))
        {
            throw new ContinuationCacheEntryExpiredException();
        }
    }

    public async Task<bool> TryShortenExpiryAsync(string key, TimeSpan maximumRemainingLifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumRemainingLifetime, TimeSpan.Zero);

        // Truncated, so the clamp is never later than the limit; under a millisecond, the entry ends now.
        long limit = maximumRemainingLifetime.Ticks / TimeSpan.TicksPerMillisecond;
        return ToConfirmation(await CallAsync(
                () => _database.ScriptEvaluateAsync(ShortenScript, [key], [limit, MaxValueBytes], OnPrimary), cancellationToken)
            .ConfigureAwait(false));
    }

    // Only absolute expirations are stored, so there is no sliding expiration to refresh.
    public void Refresh(string key) => ArgumentNullException.ThrowIfNull(key);

    public Task RefreshAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Task.CompletedTask;
    }

    // A raw one-key DEL (routed by its key) so the reply is checked here: the client's KeyDelete decoder also accepts
    // the strings "0" and "1".
    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = ToConfirmation(Call(() => _database.Execute("DEL", [(RedisKey)key], OnPrimary)));
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = ToConfirmation(await CallAsync(() => _database.ExecuteAsync("DEL", [(RedisKey)key], OnPrimary), token).ConfigureAwait(false));
    }

    private long ExpiryOf(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(options);
        if (value.Length > MaxValueBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value exceeds the provider's maximum value size.");
        }

        // ToUnixTimeMilliseconds truncates toward the earlier millisecond, the stored D = floor(E).
        return options.AbsoluteExpiration is { } expiry && options.AbsoluteExpirationRelativeToNow is null && options.SlidingExpiration is null
            ? expiry.ToUnixTimeMilliseconds()
            : throw new ArgumentException("The continuation contract stores absolute expirations only.", nameof(options));
    }

    private byte[]? ToValue(RedisResult reply)
    {
        // Exact wire types: the client folds a RESP3 verbatim string into RESP2 BulkString, so Resp2Type is not enough.
        // Both legitimate miss replies, a RESP2 null bulk string and a RESP3 null, arrive as a null single value.
        return reply.Resp3Type switch
        {
            ResultType.BulkString when (byte[]?)reply is { } value && value.Length <= MaxValueBytes => value,
            ResultType.Null when reply.Resp2Type == ResultType.BulkString => null,
            _ => throw new ContinuationCacheUnavailableException(),
        };
    }

    // Only a wire integer 0 or 1; the client folds a RESP3 Boolean into RESP2 Integer.
    private static bool ToConfirmation(RedisResult reply) => reply.Resp3Type == ResultType.Integer
        ? (long)reply switch
        {
            0 => false,
            1 => true,
            _ => throw new ContinuationCacheUnavailableException(),
        }
        : throw new ContinuationCacheUnavailableException();

    private static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new ContinuationCacheUnavailableException();
        }
    }

    // The client takes no token, so cancellation abandons the wait, not the command, which may still take effect; its
    // eventual failure is observed so it is never reported as an unobserved task exception.
    private static async Task<T> CallAsync<T>(Func<Task<T>> start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<T> call = Call(start);
        try
        {
            return await call.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = call.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            throw new ContinuationCacheUnavailableException();
        }
    }

    private static bool IsStoreFailure(Exception exception) =>
        exception is RedisException or RedisTimeoutException or RedisCommandException;
}

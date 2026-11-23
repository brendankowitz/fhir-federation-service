using Ignixa.Federation.Api.Continuations;
using Microsoft.Extensions.Caching.Distributed;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// An in-memory test fixture that implements the <see cref="IContinuationCache"/> contract under a single lock, with
/// its own backend clock and deterministic fault and gate hooks. It is evidence about the continuation component's
/// use of the contract only; it is not evidence about any deployed provider (Redis or otherwise).
/// </summary>
/// <remarks>
/// <see cref="BeforeApply"/> runs before an operation takes effect: throwing from it means the operation did not
/// happen, and awaiting a gate holds it. <see cref="AfterApply"/> runs after an operation took effect: throwing from it
/// models a lost response (the effect happened, the caller cannot know).
/// </remarks>
internal sealed class ContractContinuationCache(TimeProvider backendClock, int maxValueBytes = 1 << 20) : IContinuationCache
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _written = [];
    private int _gets;
    private int _sets;
    private int _shortens;

    public enum Operation
    {
        Get,
        Set,
        Shorten,
    }

    public int MaxValueBytes { get; } = maxValueBytes;

    public Func<Operation, CancellationToken, Task>? BeforeApply { get; set; }

    public Func<Operation, Task>? AfterApply { get; set; }

    public int Gets => Volatile.Read(ref _gets);

    public int Sets => Volatile.Read(ref _sets);

    public int Shortens => Volatile.Read(ref _shortens);

    /// <summary>Gets every key written, in write order.</summary>
    public IReadOnlyList<string> Written
    {
        get
        {
            lock (_lock)
            {
                return [.. _written];
            }
        }
    }

    /// <summary>Gets the stored expiry of a key, live or not yet purged, or <see langword="null"/> when absent.</summary>
    public DateTimeOffset? ExpiryOf(string key)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out Entry? entry) ? entry.Expiry : null;
        }
    }

    public bool Contains(string key)
    {
        lock (_lock)
        {
            return _entries.ContainsKey(key);
        }
    }

    public byte[] ValueOf(string key)
    {
        lock (_lock)
        {
            return [.. _entries[key].Value];
        }
    }

    /// <summary>Replaces a stored value in place, keeping its expiry; models store corruption or substitution.</summary>
    public void Replace(string key, byte[] value)
    {
        lock (_lock)
        {
            _entries[key] = _entries[key] with { Value = [.. value] };
        }
    }

    /// <summary>Removes a key as the backend's own eviction would.</summary>
    public void Evict(string key)
    {
        lock (_lock)
        {
            _entries.Remove(key);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        Interlocked.Increment(ref _gets);
        await Before(Operation.Get, token);
        byte[]? value = null;
        lock (_lock)
        {
            if (LiveEntry(key) is { } entry)
            {
                // The contract: a value over the provider's maximum is never materialized; it is reported as
                // unavailable, so the size is checked against the stored entry before any copy is made.
                if (entry.Value.Length > MaxValueBytes)
                {
                    throw new ContinuationCacheUnavailableException();
                }

                value = [.. entry.Value];
            }
        }

        await After(Operation.Get);
        return value;
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Interlocked.Increment(ref _sets);
        DateTimeOffset expiry = ValidateSet(value, options);
        await Before(Operation.Set, token);
        lock (_lock)
        {
            // The contract: expiry is decided at the write's linearization point, by the backend's clock.
            if (expiry <= backendClock.GetUtcNow())
            {
                throw new ContinuationCacheEntryExpiredException();
            }

            _entries[key] = new Entry([.. value], expiry);
            _written.Add(key);
        }

        await After(Operation.Set);
    }

    public async Task<bool> TryShortenExpiryAsync(string key, TimeSpan maximumRemainingLifetime, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _shortens);
        await Before(Operation.Shorten, cancellationToken);
        Entry? entry;
        lock (_lock)
        {
            entry = LiveEntry(key);
            if (entry is not null)
            {
                DateTimeOffset limit = backendClock.GetUtcNow() + maximumRemainingLifetime;
                _entries[key] = entry with { Expiry = entry.Expiry < limit ? entry.Expiry : limit };
            }
        }

        await After(Operation.Shorten);
        return entry is not null;
    }

    public byte[]? Get(string key) => throw new NotSupportedException("The continuation component uses only asynchronous members.");

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
        throw new NotSupportedException("The continuation component uses only asynchronous members.");

    // No sliding expiration is ever stored, so refreshing has nothing to extend.
    public void Refresh(string key)
    {
    }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => Evict(key);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Evict(key);
        return Task.CompletedTask;
    }

    private DateTimeOffset ValidateSet(byte[] value, DistributedCacheEntryOptions options)
    {
        if (value.Length > MaxValueBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value exceeds the provider's maximum value size.");
        }

        return options.AbsoluteExpiration is { } expiry && options.AbsoluteExpirationRelativeToNow is null && options.SlidingExpiration is null
            ? expiry
            : throw new ArgumentException("The continuation contract stores absolute expirations only.", nameof(options));
    }

    // Caller holds the lock. An expired entry is purged, as a backend would, so it can never be read or shortened.
    private Entry? LiveEntry(string key)
    {
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return null;
        }

        if (backendClock.GetUtcNow() < entry.Expiry)
        {
            return entry;
        }

        _entries.Remove(key);
        return null;
    }

    private Task Before(Operation operation, CancellationToken token) => BeforeApply?.Invoke(operation, token) ?? Task.CompletedTask;

    private Task After(Operation operation) => AfterApply?.Invoke(operation) ?? Task.CompletedTask;

    private sealed record Entry(byte[] Value, DateTimeOffset Expiry);
}

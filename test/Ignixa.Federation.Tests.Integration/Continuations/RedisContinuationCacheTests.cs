using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ignixa.Federation.Api.Continuations;
using Microsoft.Extensions.Caching.Distributed;
using Shouldly;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// <see cref="RedisContinuationCache"/> at its client boundary, against a scripted <see cref="IDatabase"/>: argument
/// defects, key and primary addressing, reply interpretation, failure translation, and cancellation. These run in the
/// regular suite without a process. They say nothing about Redis semantics (clock, expiry, atomicity, the server-side
/// bound); <see cref="RedisContinuationCacheInteropTests"/> covers those against a real Redis process.
/// </summary>
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "The synchronous IDistributedCache members are under test.")]
public sealed class RedisContinuationCacheTests
{
    private const int Limit = 16;
    private const string Key = "Ignixa.Federation.Continuations.v1:key";
    private const string Detail = "detail-that-must-not-escape";

    private static readonly DistributedCacheEntryOptions Later = new() { AbsoluteExpiration = DateTimeOffset.UtcNow.AddHours(1) };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string UnavailableMessage = new ContinuationCacheUnavailableException().Message;

    // Every member, sync and async, with a reply that the member accepts as a success.
    private static readonly Dictionary<string, (Func<RedisContinuationCache, Task> Run, object Reply)> Members = new()
    {
        ["Get"] = (c => Task.FromResult(c.Get(Key)), ScriptedRedisDatabase.NullBulk()),
        ["GetAsync"] = (c => c.GetAsync(Key, Ct), ScriptedRedisDatabase.NullBulk()),
        ["Set"] = (c => { c.Set(Key, [1], Later); return Task.CompletedTask; }, ScriptedRedisDatabase.Integer(1)),
        ["SetAsync"] = (c => c.SetAsync(Key, [1], Later, Ct), ScriptedRedisDatabase.Integer(1)),
        ["TryShortenExpiryAsync"] = (c => c.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(60), Ct), ScriptedRedisDatabase.Integer(1)),
        ["Remove"] = (c => { c.Remove(Key); return Task.CompletedTask; }, true),
        ["RemoveAsync"] = (c => c.RemoveAsync(Key, Ct), true),
    };

    public static TheoryData<string> MemberNames => [.. Members.Keys];

    public static TheoryData<string, string, bool> Failures
    {
        get
        {
            var data = new TheoryData<string, string, bool>();
            foreach (string member in Members.Keys)
            {
                foreach (string failure in new[] { "connection", "timeout", "server", "command" })
                {
                    data.Add(member, failure, false);
                    data.Add(member, failure, true);
                }
            }

            return data;
        }
    }

    [Fact]
    public void Constructor_RejectsAMissingDatabase() =>
        Should.Throw<ArgumentNullException>(() => new RedisContinuationCache(null!, Limit));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RedisContinuationCache.MaxSupportedValueBytes + 1)]
    public void Constructor_RejectsAMaximumOutsideTheRedisStringRange(int maxValueBytes) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new RedisContinuationCache(ScriptedRedisDatabase.Create().Database, maxValueBytes));

    [Theory]
    [InlineData(1)]
    [InlineData(RedisContinuationCache.MaxSupportedValueBytes)]
    public void Constructor_ExposesTheExplicitMaximum(int maxValueBytes) =>
        new RedisContinuationCache(ScriptedRedisDatabase.Create().Database, maxValueBytes).MaxValueBytes.ShouldBe(maxValueBytes);

    [Fact]
    public async Task ArgumentDefects_AreThrownBeforeAnyStoreCall()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create();
        var cache = new RedisContinuationCache(database, Limit);
        byte[] tooLarge = new byte[Limit + 1];

        Should.Throw<ArgumentNullException>(() => cache.Get(null!));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.GetAsync(null!, Ct));
        Should.Throw<ArgumentNullException>(() => cache.Set(null!, [1], Later));
        Should.Throw<ArgumentNullException>(() => cache.Set(Key, null!, Later));
        Should.Throw<ArgumentNullException>(() => cache.Set(Key, [1], null!));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.SetAsync(null!, [1], Later, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.SetAsync(Key, null!, Later, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.SetAsync(Key, [1], null!, Ct));
        Should.Throw<ArgumentOutOfRangeException>(() => cache.Set(Key, tooLarge, Later));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => cache.SetAsync(Key, tooLarge, Later, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.TryShortenExpiryAsync(null!, TimeSpan.FromSeconds(1), Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => cache.TryShortenExpiryAsync(Key, TimeSpan.Zero, Ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => cache.TryShortenExpiryAsync(Key, TimeSpan.FromTicks(-1), Ct));
        Should.Throw<ArgumentNullException>(() => cache.Remove(null!));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.RemoveAsync(null!, Ct));
        Should.Throw<ArgumentNullException>(() => cache.Refresh(null!));
        await Should.ThrowAsync<ArgumentNullException>(() => cache.RefreshAsync(null!, Ct));

        DateTimeOffset at = DateTimeOffset.UtcNow.AddHours(1);
        foreach (DistributedCacheEntryOptions options in new DistributedCacheEntryOptions[]
        {
            new(),
            new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
            new() { SlidingExpiration = TimeSpan.FromMinutes(1) },
            new() { AbsoluteExpiration = at, SlidingExpiration = TimeSpan.FromMinutes(1) },
            new() { AbsoluteExpiration = at, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) },
        })
        {
            Should.Throw<ArgumentException>(() => cache.Set(Key, [1], options));
            await Should.ThrowAsync<ArgumentException>(() => cache.SetAsync(Key, [1], options, Ct));
        }

        script.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_AcceptsAValueAtTheMaximum()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.Integer(1));
        var cache = new RedisContinuationCache(database, Limit);

        cache.Set(Key, new byte[Limit], Later);
        await cache.SetAsync(Key, new byte[Limit], Later, Ct);

        script.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task EveryMember_AddressesTheCallersKeyOnThePrimary()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create();
        var cache = new RedisContinuationCache(database, Limit);

        foreach ((Func<RedisContinuationCache, Task> run, object reply) in Members.Values)
        {
            script.Respond = _ => reply;
            await run(cache);
        }

        script.Calls.Count.ShouldBe(Members.Count);
        script.Calls.ShouldAllBe(call => call.Keys.Count == 1 && call.Keys[0] == (RedisKey)Key);
        script.Calls.ShouldAllBe(call => call.Flags.HasFlag(CommandFlags.DemandMaster));
    }

    [Fact]
    public async Task Refresh_HasNothingToExtendAndMakesNoStoreCall()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create();
        var cache = new RedisContinuationCache(database, Limit);

        cache.Refresh(Key);
        await cache.RefreshAsync(Key, Ct);

        script.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_DistinguishesAMissFromAnEmptyValue()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.NullBulk());
        var cache = new RedisContinuationCache(database, Limit);

        cache.Get(Key).ShouldBeNull();
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();

        script.Respond = _ => ScriptedRedisDatabase.Bulk([]);
        cache.Get(Key).ShouldBe([]);
        (await cache.GetAsync(Key, Ct)).ShouldBe([]);
    }

    [Fact]
    public async Task Get_ReturnsAValueAtTheMaximum()
    {
        byte[] value = [.. Enumerable.Range(0, Limit).Select(i => (byte)i)];
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.Bulk(value));
        var cache = new RedisContinuationCache(database, Limit);

        cache.Get(Key).ShouldBe(value);
        (await cache.GetAsync(Key, Ct)).ShouldBe(value);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("integer")]
    [InlineData("status")]
    [InlineData("array")]
    public async Task Get_NonconformingReply_IsUnavailable(string shape)
    {
        RedisResult reply = shape switch
        {
            "oversize" => ScriptedRedisDatabase.Bulk(new byte[Limit + 1]),
            "integer" => ScriptedRedisDatabase.Integer(0),
            "status" => RedisResult.Create((RedisValue)"OK", ResultType.SimpleString),
            _ => RedisResult.Create([ScriptedRedisDatabase.Bulk([1])]),
        };
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => reply);
        var cache = new RedisContinuationCache(database, Limit);

        ShouldBeUnavailable(Should.Throw<ContinuationCacheUnavailableException>(() => cache.Get(Key)));
        ShouldBeUnavailable(await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.GetAsync(Key, Ct)));
    }

    [Fact]
    public async Task Set_RefusedAsExpiredByTheServer_ThrowsTheExpiredRefusal()
    {
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.Integer(0));
        var cache = new RedisContinuationCache(database, Limit);

        Should.Throw<ContinuationCacheEntryExpiredException>(() => cache.Set(Key, [1], Later)).InnerException.ShouldBeNull();
        (await Should.ThrowAsync<ContinuationCacheEntryExpiredException>(() => cache.SetAsync(Key, [1], Later, Ct))).InnerException.ShouldBeNull();
    }

    [Theory]
    [InlineData("two")]
    [InlineData("negative")]
    [InlineData("bulk")]
    [InlineData("null")]
    public async Task Set_NonconformingReply_IsUnavailable(string shape)
    {
        RedisResult reply = Nonconforming(shape);
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => reply);
        var cache = new RedisContinuationCache(database, Limit);

        ShouldBeUnavailable(Should.Throw<ContinuationCacheUnavailableException>(() => cache.Set(Key, [1], Later)));
        ShouldBeUnavailable(await Should.ThrowAsync<ContinuationCacheUnavailableException>(() => cache.SetAsync(Key, [1], Later, Ct)));
    }

    [Fact]
    public async Task TryShorten_ReportsOnlyAConfirmedMissAsFalse()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.Integer(0));
        var cache = new RedisContinuationCache(database, Limit);

        (await cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(60), Ct)).ShouldBeFalse();

        script.Respond = _ => ScriptedRedisDatabase.Integer(1);
        (await cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(60), Ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("two")]
    [InlineData("negative")]
    [InlineData("bulk")]
    [InlineData("null")]
    public async Task TryShorten_NonconformingReply_IsUnavailable(string shape)
    {
        RedisResult reply = Nonconforming(shape);
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => reply);
        var cache = new RedisContinuationCache(database, Limit);

        ShouldBeUnavailable(await Should.ThrowAsync<ContinuationCacheUnavailableException>(
            () => cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(60), Ct)));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task StoreFailures_AreUnavailableAndCarryNoDetail(string member, string failure, bool thrownSynchronously)
    {
        Exception fault = failure switch
        {
            "connection" => new RedisConnectionException(ConnectionFailureType.SocketFailure, Detail),
            "timeout" => new RedisTimeoutException(Detail, CommandStatus.Sent),
            "server" => new RedisServerException("ERR " + Detail),
            _ => new RedisCommandException(Detail),
        };
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => thrownSynchronously ? throw fault : Task.FromException<RedisResult>(fault));
        var cache = new RedisContinuationCache(database, Limit);

        ContinuationCacheUnavailableException translated =
            await Should.ThrowAsync<ContinuationCacheUnavailableException>(async () => await Members[member].Run(cache));

        ShouldBeUnavailable(translated);
    }

    [Theory]
    [MemberData(nameof(MemberNames))]
    public async Task OtherExceptions_ArePropagatedAsDefects(string member)
    {
        var defect = new InvalidOperationException(Detail);
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => throw defect);
        var cache = new RedisContinuationCache(database, Limit);

        (await Should.ThrowAsync<InvalidOperationException>(async () => await Members[member].Run(cache))).ShouldBeSameAs(defect);
    }

    [Fact]
    public async Task PreCanceledCalls_SendNothing()
    {
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create(_ => ScriptedRedisDatabase.Integer(1));
        var cache = new RedisContinuationCache(database, Limit);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => cache.GetAsync(Key, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.SetAsync(Key, [1], Later, canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(1), canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => cache.RemoveAsync(Key, canceled.Token));

        script.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("GetAsync")]
    [InlineData("SetAsync")]
    [InlineData("TryShortenExpiryAsync")]
    [InlineData("RemoveAsync")]
    public async Task CancellationWhileACallIsPending_IsPromptAndNotReportedAsUnavailable(string member)
    {
        var pending = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        (IDatabase database, ScriptedRedisDatabase script) = ScriptedRedisDatabase.Create(_ => pending.Task);
        var cache = new RedisContinuationCache(database, Limit);
        using var cts = new CancellationTokenSource();

        Task call = Pending(cache, member, cts.Token);
        script.Calls.Count.ShouldBe(1);
        call.IsCompleted.ShouldBeFalse();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        pending.Task.IsCompleted.ShouldBeFalse();
        pending.SetResult(member == "RemoveAsync" ? true : ScriptedRedisDatabase.Integer(1));
    }

    [Fact]
    public async Task ACanceledCallThatFailsLater_LeavesNoUnobservedException()
    {
        string marker = Guid.NewGuid().ToString("N");
        bool unobserved = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message.Contains(marker, StringComparison.Ordinal)))
            {
                unobserved = true;
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await CancelThenFailAsync(marker);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            unobserved.ShouldBeFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CancelThenFailAsync(string marker)
    {
        var pending = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        (IDatabase database, _) = ScriptedRedisDatabase.Create(_ => pending.Task);
        var cache = new RedisContinuationCache(database, Limit);
        using var cts = new CancellationTokenSource();

        Task<bool> call = cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(1), cts.Token);
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        pending.SetException(new RedisConnectionException(ConnectionFailureType.SocketFailure, marker));
        await Task.Delay(50, Ct);
    }

    private static Task Pending(RedisContinuationCache cache, string member, CancellationToken token) => member switch
    {
        "GetAsync" => cache.GetAsync(Key, token),
        "SetAsync" => cache.SetAsync(Key, [1], Later, token),
        "TryShortenExpiryAsync" => cache.TryShortenExpiryAsync(Key, TimeSpan.FromSeconds(1), token),
        _ => cache.RemoveAsync(Key, token),
    };

    private static RedisResult Nonconforming(string shape) => shape switch
    {
        "two" => ScriptedRedisDatabase.Integer(2),
        "negative" => ScriptedRedisDatabase.Integer(-1),
        "bulk" => ScriptedRedisDatabase.Bulk([1]),
        _ => ScriptedRedisDatabase.NullBulk(),
    };

    private static void ShouldBeUnavailable(ContinuationCacheUnavailableException exception)
    {
        exception.InnerException.ShouldBeNull();
        exception.Message.ShouldBe(UnavailableMessage);
        exception.Message.ShouldNotContain(Detail);
        exception.Message.ShouldNotContain(Key);
    }
}

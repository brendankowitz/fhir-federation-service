using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Core.UnitTests.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>
/// A scripted, instrumented <see cref="IShardClient"/>. Each first page is scripted per shard and each continuation per
/// cursor; a handler may await a gate. Records every call, the calls in flight, the peak in flight and completion order.
/// </summary>
internal sealed class FakeShardClient : IShardClient
{
    private readonly Lock _lock = new();
    private readonly Dictionary<ShardId, Func<ShardSearchRequest, CancellationToken, Task<ShardPage>>> _searches = [];
    private readonly Dictionary<ShardCursor, Func<CancellationToken, Task<ShardPage>>> _continues = [];
    private readonly HashSet<ShardId> _synchronousFailures = [];
    private readonly List<FakeCall> _calls = [];
    private readonly List<FakeCall> _finished = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _startWaiters = [];
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>Gets every call that started, in start order.</summary>
    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Gets every call that finished, in completion order.</summary>
    public IReadOnlyList<FakeCall> Finished
    {
        get
        {
            lock (_lock)
            {
                return [.. _finished];
            }
        }
    }

    public int InFlight
    {
        get
        {
            lock (_lock)
            {
                return _inFlight;
            }
        }
    }

    public int MaxInFlight
    {
        get
        {
            lock (_lock)
            {
                return _maxInFlight;
            }
        }
    }

    public void OnSearch(ShardId shard, Func<ShardSearchRequest, CancellationToken, Task<ShardPage>> respond)
    {
        lock (_lock)
        {
            _searches[shard] = respond;
        }
    }

    public void OnSearch(int ordinal, ShardPage page) => OnSearch(Fixture.Shard(ordinal), (_, _) => Task.FromResult(page));

    /// <summary>Scripts a first page that waits for <paramref name="gate"/>, honouring cancellation.</summary>
    public void OnSearch(int ordinal, TaskCompletionSource gate, ShardPage page) =>
        OnSearch(Fixture.Shard(ordinal), async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return page;
        });

    public void OnSearchThrows(int ordinal, Exception exception) =>
        OnSearch(Fixture.Shard(ordinal), (_, _) => Task.FromException<ShardPage>(exception));

    /// <summary>Makes <see cref="SearchAsync"/> throw before returning a task, as a broken client might.</summary>
    public void OnSearchThrowsSynchronously(int ordinal)
    {
        lock (_lock)
        {
            _synchronousFailures.Add(Fixture.Shard(ordinal));
        }
    }

    public void OnContinue(ShardCursor cursor, Func<CancellationToken, Task<ShardPage>> respond)
    {
        lock (_lock)
        {
            _continues[cursor] = respond;
        }
    }

    public void OnContinue(ShardCursor cursor, ShardPage page) => OnContinue(cursor, _ => Task.FromResult(page));

    public void OnContinue(ShardCursor cursor, TaskCompletionSource gate, ShardPage page) =>
        OnContinue(cursor, async cancellationToken =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return page;
        });

    /// <summary>
    /// Serves <paramref name="ids"/> like a keyset-paging backend: each page holds the sent <c>_count</c> plus
    /// <paramref name="overReturn"/> entries, and <c>next</c> carries the offset and the original count.
    /// </summary>
    public void Serve(int ordinal, string[] ids, int overReturn = 0, BackendProfile? profile = null)
    {
        BackendProfile backend = profile ?? TestShards.MicrosoftFhirServer;
        OnSearch(Fixture.Shard(ordinal), (request, _) => Task.FromResult(Slice(request, backend, ids, 0, overReturn)));
    }

    /// <summary>Completes when at least <paramref name="count"/> calls have started.</summary>
    public Task WhenStarted(int count)
    {
        lock (_lock)
        {
            if (_calls.Count >= count)
            {
                return Task.CompletedTask;
            }

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startWaiters.Add((count, reached));
            return reached.Task;
        }
    }

    public Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
    {
        Func<ShardSearchRequest, CancellationToken, Task<ShardPage>>? respond;
        lock (_lock)
        {
            if (_synchronousFailures.Contains(request.Shard))
            {
                throw new InvalidOperationException("Synchronous client failure.");
            }

            _searches.TryGetValue(request.Shard, out respond);
        }

        return RunAsync(
            new FakeCall(request, null),
            respond is null ? Unscripted : token => respond(request, token),
            cancellationToken);
    }

    public Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<ShardPage>>? respond;
        lock (_lock)
        {
            _continues.TryGetValue(cursor, out respond);
        }

        return RunAsync(new FakeCall(null, cursor), respond ?? Unscripted, cancellationToken);
    }

    private static Task<ShardPage> Unscripted(CancellationToken cancellationToken) =>
        Task.FromException<ShardPage>(new InvalidOperationException("The test did not script this call."));

    private async Task<ShardPage> RunAsync(FakeCall call, Func<CancellationToken, Task<ShardPage>> respond, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add(call);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            foreach ((int count, TaskCompletionSource reached) in _startWaiters.Where(w => _calls.Count >= w.Count))
            {
                reached.TrySetResult();
            }
        }

        try
        {
            return await respond(cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                _inFlight--;
                _finished.Add(call);
            }

            call.MarkFinished();
        }
    }

    private ShardPage Slice(ShardSearchRequest origin, BackendProfile profile, string[] ids, int offset, int overReturn)
    {
        int end = Math.Min(ids.Length, offset + origin.PageSize + overReturn);
        ShardCursor? next = null;
        if (end < ids.Length)
        {
            next = ShardCursor.Next(origin, profile, $"https://{origin.Shard}.example/fhir/Patient?ct={end}-{origin.PageSize}");
            OnContinue(next, _ => Task.FromResult(Slice(origin, profile, ids, end, overReturn)));
        }

        return new ShardPage(ids[offset..end].Select(id => Fixture.Entry(Fixture.Key(origin.Shard, id))), next, null);
    }
}

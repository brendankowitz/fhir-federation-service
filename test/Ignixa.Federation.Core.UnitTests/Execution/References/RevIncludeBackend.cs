using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution.References;

/// <summary>
/// An in-memory shard boundary for reverse include searches: first pages are scripted per shard and searched source type,
/// continuations per cursor, over an instrumented <see cref="FakeShardClient"/>. No HTTP.
/// </summary>
internal sealed class RevIncludeBackend
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(ShardId, ResourceType), Func<ShardSearchRequest, CancellationToken, Task<ShardPage>>> _searches = [];

    public FakeShardClient Client { get; } = new();

    public void OnSearch(ShardId shard, ResourceType type, Func<ShardSearchRequest, CancellationToken, Task<ShardPage>> respond)
    {
        lock (_lock)
        {
            _searches[(shard, type)] = respond;
        }

        Client.OnSearch(shard, (request, cancellationToken) => Respond(request, cancellationToken));
    }

    public void OnSearch(ShardId shard, ResourceType type, ShardPage page) => OnSearch(shard, type, (_, _) => Task.FromResult(page));

    /// <summary>Scripts a first page that waits for <paramref name="gate"/>, honouring cancellation.</summary>
    public void OnSearch(ShardId shard, ResourceType type, TaskCompletionSource gate, ShardPage page) =>
        OnSearch(shard, type, async (_, cancellationToken) =>
        {
            await gate.Task.WaitAsync(cancellationToken);
            return page;
        });

    public void OnSearchThrows(ShardId shard, ResourceType type, Exception exception) =>
        OnSearch(shard, type, (_, _) => Task.FromException<ShardPage>(exception));

    public void OnContinue(ShardCursor cursor, ShardPage page) => Client.OnContinue(cursor, page);

    public void OnContinue(ShardCursor cursor, Func<CancellationToken, Task<ShardPage>> respond) => Client.OnContinue(cursor, respond);

    private Task<ShardPage> Respond(ShardSearchRequest request, CancellationToken cancellationToken)
    {
        Func<ShardSearchRequest, CancellationToken, Task<ShardPage>>? respond;
        lock (_lock)
        {
            _searches.TryGetValue((request.Shard, request.Type), out respond);
        }

        return respond is null
            ? Task.FromException<ShardPage>(new InvalidOperationException("The test did not script this search."))
            : respond(request, cancellationToken);
    }
}

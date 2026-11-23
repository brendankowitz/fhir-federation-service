using System.Collections.Concurrent;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Reads;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Search;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>What the host passed to one <see cref="IShardReadBinding.ReadAsync"/> call.</summary>
/// <remarks><see cref="TokenIsBudgetToken"/> is sampled during the call because the budget's token source is disposed with the request.</remarks>
internal sealed record ReadCall(ShardRegistry Registry, ResourceKey Key, RequestBudget Budget, bool TokenIsBudgetToken, CancellationToken Token);

/// <summary>
/// A scripted binding for the collision corpus: by default it serves the exact seed bytes of the requested owner,
/// charging the budget as the binding contract requires, and records every call.
/// </summary>
internal sealed class ScriptedReadBinding : IShardReadBinding
{
    private readonly ConcurrentQueue<ReadCall> _calls = new();

    public IReadOnlyList<ReadCall> Calls => [.. _calls];

    /// <summary>Gets or sets the script that replaces the default corpus read; it may throw.</summary>
    public Func<ReadCall, ShardReadResult>? Script { get; set; }

    public ValueTask<ShardReadResult> ReadAsync(ShardRegistry registry, ResourceKey key, RequestBudget budget, CancellationToken cancellationToken)
    {
        var call = new ReadCall(registry, key, budget, cancellationToken == budget.Token, cancellationToken);
        _calls.Enqueue(call);
        return ValueTask.FromResult(Script is { } script ? script(call) : Corpus(call));
    }

    /// <summary>Reads from the seeded corpus: the owner's exact bytes, or not found.</summary>
    public static ShardReadResult Corpus(ReadCall call)
    {
        call.Budget.BeforeBackendCall();
        SearchWorld.SeedResource? seed = SearchWorld.Resources.TryGetValue(call.Key.Shard.Value, out IReadOnlyList<SearchWorld.SeedResource>? seeds)
            ? seeds.FirstOrDefault(r => r.Type == call.Key.Type.Value && r.Id == call.Key.Id.Value)
            : null;
        if (seed is null)
        {
            return ShardReadResult.NotFound;
        }

        call.Budget.OnResponseBytesConsumed(seed.Json.Length);
        return ShardReadResult.Found(new ShardEntry(call.Key, null, null, seed.Json));
    }
}

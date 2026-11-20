using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Core.UnitTests.Execution;

/// <summary>One call the fake received.</summary>
internal sealed class FakeCall(ShardSearchRequest? search, ShardCursor? cursor)
{
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets the search sent, for a first-page call.</summary>
    public ShardSearchRequest? Search { get; } = search;

    /// <summary>Gets the cursor redeemed, for a continuation call.</summary>
    public ShardCursor? Cursor { get; } = cursor;

    public ShardId Shard => Search?.Shard ?? Cursor!.Shard;

    /// <summary>Gets a task that completes when the call has returned or thrown.</summary>
    public Task Finished => _finished.Task;

    public override string ToString() => Search is not null ? $"search {Shard} count={Search.PageSize}" : $"continue {Shard}";

    internal void MarkFinished() => _finished.TrySetResult();
}

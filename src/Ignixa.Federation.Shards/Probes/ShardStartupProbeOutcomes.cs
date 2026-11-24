using System.Collections.Immutable;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Probes;

/// <summary>
/// The startup probe plan and its published outcomes: one step per registered shard and wire method, each holding what the
/// strict-handling and replay probes showed so far. Outcomes are config-derived names only; no link, body, credential or
/// exception is kept.
/// </summary>
/// <remarks>
/// The plan is fixed at construction. A single worker publishes after each probe by swapping in a new immutable snapshot, so
/// a reader sees one coherent state and never blocks the worker.
/// </remarks>
internal sealed class ShardStartupProbeOutcomes
{
    private ShardStartupProbeSnapshot _current;

    /// <summary>Plans GET for every shard and POST where the profile offers POST search, in registry order.</summary>
    /// <exception cref="ArgumentException">The selection is not exactly one resource type for each registered shard.</exception>
    public ShardStartupProbeOutcomes(ShardRegistry registry, IReadOnlyDictionary<ShardId, ResourceType> types)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count != registry.Shards.Count || registry.Shards.Any(shard => !types.ContainsKey(shard.Id)))
        {
            throw new ArgumentException("The startup probe selection must hold exactly one resource type for each registered shard.", nameof(types));
        }

        var steps = new List<ShardStartupProbeStep>();
        foreach (ShardDescriptor shard in registry.Shards)
        {
            steps.Add(new(shard, types[shard.Id], ShardSearchMethod.Get));
            if (shard.Profile.SupportsPostSearch)
            {
                steps.Add(new(shard, types[shard.Id], ShardSearchMethod.PostSearch));
            }
        }

        Steps = [.. steps];
        _current = new(steps.Select(step => new ShardStartupProbeEntry(step.Shard.Alias, step.Method, null, null)).ToImmutableList());
    }

    /// <summary>Gets the fixed, ordered work.</summary>
    public IReadOnlyList<ShardStartupProbeStep> Steps { get; }

    /// <summary>Gets the latest published snapshot.</summary>
    public ShardStartupProbeSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Publishes what one step has shown. Called only by the single worker.</summary>
    public void Record(int step, StrictHandlingProbeOutcome strict, LastUpdatedReplayProbeOutcome? replay)
    {
        ImmutableList<ShardStartupProbeEntry> entries = _current.Entries;
        Volatile.Write(ref _current, new(entries.SetItem(step, entries[step] with { Strict = strict, Replay = replay })));
    }
}

/// <summary>One shard, resource type and starting method to probe.</summary>
internal sealed record ShardStartupProbeStep(ShardDescriptor Shard, ResourceType Type, ShardSearchMethod Method);

/// <summary>What the probes showed for one shard and method; <see langword="null"/> means not yet tested.</summary>
internal sealed record ShardStartupProbeEntry(
    ShardAlias Alias,
    ShardSearchMethod Method,
    StrictHandlingProbeOutcome? Strict,
    LastUpdatedReplayProbeOutcome? Replay);

/// <summary>One immutable view of every required method's outcomes.</summary>
internal sealed class ShardStartupProbeSnapshot(ImmutableList<ShardStartupProbeEntry> entries)
{
    /// <summary>Gets the entries in plan order.</summary>
    public ImmutableList<ShardStartupProbeEntry> Entries { get; } = entries;

    /// <summary>Gets whether every required method proved strict handling and showed a replay.</summary>
    public bool IsReady => Entries.Count > 0 &&
        Entries.All(entry => entry.Strict == StrictHandlingProbeOutcome.Proven &&
                             entry.Replay == LastUpdatedReplayProbeOutcome.Observed);
}

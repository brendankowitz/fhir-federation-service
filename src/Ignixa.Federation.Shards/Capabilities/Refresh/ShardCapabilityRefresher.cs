using System.Collections.Immutable;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Refreshes registered shard advertisements on startup and on a ten-minute schedule (design spec §9.1).
/// </summary>
/// <remarks>
/// <para>One loop writes; any thread reads <see cref="Current"/> without locking. Fetches run one at a time, in registry
/// order, including during startup: a global cap of one is deliberate, so a slow sweep delays later shards rather than
/// fanning out. Each fetch is published as soon as it ends.</para>
/// <para>Sweeps never overlap. The clock starts when <see cref="RunAsync"/> starts, so a sweep that outlasts the interval
/// is followed at once by one sweep, not a backlog: missed ticks coalesce.</para>
/// <para>A classified failure keeps any earlier evidence and records only the classification; with no evidence the entry
/// stays incomplete. A success replaces the evidence and clears the failure.</para>
/// </remarks>
public sealed class ShardCapabilityRefresher
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly ShardRegistry _registry;
    private readonly ShardCapabilityFetch _fetch;
    private readonly TimeProvider _timeProvider;
    private ShardCapabilitySnapshot _current;
    private int _running;

    /// <summary>Creates a refresher whose snapshot starts with one pending entry per registered shard.</summary>
    /// <param name="registry">The shards to refresh.</param>
    /// <param name="fetch">Fetches one shard's advertisement; it must honor the contract in <see cref="ShardCapabilityFetch"/>.</param>
    /// <param name="timeProvider">Supplies the sweep timer.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ShardCapabilityRefresher(ShardRegistry registry, ShardCapabilityFetch fetch, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _registry = registry;
        _fetch = fetch;
        _timeProvider = timeProvider;
        _current = new ShardCapabilitySnapshot(
            registry,
            registry.Shards.ToImmutableDictionary(s => s.Id, _ => new ShardCapabilityEntry(evidence: null, failure: null)));
    }

    /// <summary>Gets the latest published snapshot; it never changes, so keep or compare it freely.</summary>
    public ShardCapabilitySnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Reads the evidence currently published for <paramref name="shard"/>: the instance itself, not a copy. Null when the
    /// shard is not this refresher's exact registered descriptor or nothing has been fetched.
    /// </summary>
    internal ShardCapabilityEvidence? EvidenceFor(ShardDescriptor shard) =>
        _registry.TryGetById(shard.Id, out ShardDescriptor? registered) && ReferenceEquals(registered, shard)
            ? Current.Shards[shard.Id].Evidence
            : null;

    /// <summary>Runs sweeps until <paramref name="stoppingToken"/> is cancelled.</summary>
    /// <param name="stoppingToken">Stops the loop, including a fetch in progress.</param>
    /// <returns>A task that completes normally on shutdown. Results are discarded when cancellation is observed before publication.</returns>
    /// <exception cref="InvalidOperationException">The loop is already running on this instance.</exception>
    /// <exception cref="Exception">A programming error from the fetch seam is rethrown, never recorded as a shard failure.</exception>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("The capability refresher is already running.");
        }

        try
        {
            // Created before the first sweep, so the clock starts at startup and a slow sweep does not push the next one back.
            using var timer = new PeriodicTimer(Interval, _timeProvider);
            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown, not a failure: nothing is recorded and the task ends normally.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        foreach (ShardDescriptor shard in _registry.Shards)
        {
            stoppingToken.ThrowIfCancellationRequested();
            ShardCapabilityEntry entry;
            try
            {
                ShardCapabilityEvidence evidence = await _fetch(shard, stoppingToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The capability fetch returned no evidence.");
                entry = new ShardCapabilityEntry(evidence, failure: null);
            }
            catch (ShardCallException e)
            {
                entry = new ShardCapabilityEntry(Current.Shards[shard.Id].Evidence, e.Failure);
            }

            // Check again in case the fetch completed despite cancellation.
            stoppingToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _current, Current.With(shard.Id, entry));
        }
    }
}

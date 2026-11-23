using System.Collections.Concurrent;
using System.Net;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Execution;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>What the host handed the strategy's initial operation.</summary>
internal sealed record ForwardResolveCall(
    QueryPlan Plan,
    SearchCapabilities Capabilities,
    IReadOnlySet<ResourceType> Readable,
    IShardClient Client,
    LimitOptions Limits,
    TimeoutOptions Timeouts,
    Func<ShardSearchRequest, bool> Fits,
    int FetchAllowance,
    long PayloadByteAllowance,
    CancellationToken Token);

/// <summary>What the host handed the strategy's pure replay operation: there is deliberately no client.</summary>
internal sealed record ForwardRebuildCall(
    QueryPlan Plan,
    SearchCapabilities Capabilities,
    IReadOnlyList<ShardSearchRequest> Saved,
    int MaxChainKeys,
    Func<ShardSearchRequest, bool> Fits);

/// <summary>
/// A scripted host strategy. Its default initial operation is a minimal stand-in for Core's join, only so the real
/// <c>Program</c> can be driven: it searches the chain's target terminal on every shard through the request's client and gives
/// each owner <c>Patient/{id}</c> plus the absolute form for its own witnesses and only the absolute form for the others'.
/// The live join and reconstruction belong to Core; these tests only pin what the host does around them.
/// </summary>
internal sealed class ScriptedDistributedForward : IDistributedForwardSearch
{
    private static readonly ResourceType Patient = ResourceType.Parse("Patient");

    public ConcurrentQueue<ForwardResolveCall> Resolves { get; } = new();

    public ConcurrentQueue<ForwardRebuildCall> Rebuilds { get; } = new();

    public Func<ForwardResolveCall, Task<(IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)?>>? ResolveScript { get; set; }

    public Func<ForwardRebuildCall, IReadOnlyList<ShardSearchRequest>?>? RebuildScript { get; set; }

    public Task<(IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)?> ResolveAsync(
        QueryPlan plan,
        SearchCapabilities capabilities,
        IReadOnlySet<ResourceType> readableTypes,
        IShardClient client,
        LimitOptions limits,
        TimeoutOptions timeouts,
        Func<ShardSearchRequest, bool> fits,
        int fetchAllowance,
        long payloadByteAllowance,
        CancellationToken cancellationToken)
    {
        var call = new ForwardResolveCall(plan, capabilities, readableTypes, client, limits, timeouts, fits, fetchAllowance, payloadByteAllowance, cancellationToken);
        Resolves.Enqueue(call);
        return (ResolveScript ?? DiscoverAsync)(call);
    }

    public IReadOnlyList<ShardSearchRequest>? Rebuild(
        QueryPlan plan,
        SearchCapabilities capabilities,
        IReadOnlyList<ShardSearchRequest> savedStreams,
        int maxChainKeys,
        Func<ShardSearchRequest, bool> fits)
    {
        var call = new ForwardRebuildCall(plan, capabilities, savedStreams, maxChainKeys, fits);
        Rebuilds.Enqueue(call);
        return (RebuildScript ?? Faithful)(call);
    }

    /// <summary>Searches the target terminal on every shard and builds every owner's canonical stream.</summary>
    public static async Task<(IReadOnlyList<ShardSearchRequest> Streams, int RemainingFetches)?> DiscoverAsync(ForwardResolveCall call)
    {
        ParameterOccurrence terminal = call.Plan.Occurrences.Single(o => o.Kind == OccurrenceKind.ForwardChain).Hop!.Terminal!;
        var witnesses = new List<(ShardDescriptor Shard, string Id)>();
        int used = 0;
        foreach (ShardDescriptor shard in call.Capabilities.Registry.Shards)
        {
            used++;
            ShardPage page;
            try
            {
                page = await call.Client.SearchAsync(new ShardSearchRequest(shard.Id, Patient, [terminal], 100, ShardSearchMethod.Get), call.Token);
            }
            catch (HttpRequestException ex)
            {
                throw new ShardFailureException(shard.Id, ShardFailureReason.Failed, ex);
            }

            witnesses.AddRange(page.Entries.Select(e => (shard, e.Key.Id.Value)));
        }

        return witnesses.Count == 0
            ? ([], call.FetchAllowance - used)
            : (Streams(call.Plan, call.Capabilities.Registry, witnesses), call.FetchAllowance - used);
    }

    /// <summary>The canonical ordered streams for the given witnesses, one per registered owner, with no I/O.</summary>
    public static IReadOnlyList<ShardSearchRequest> Streams(
        QueryPlan plan, ShardRegistry registry, IReadOnlyList<(ShardDescriptor Shard, string Id)> witnesses) =>
        [.. registry.Shards.Select(owner => Stream(plan, owner, witnesses))];

    private static ShardSearchRequest Stream(QueryPlan plan, ShardDescriptor owner, IReadOnlyList<(ShardDescriptor Shard, string Id)> witnesses)
    {
        string[] forms =
        [
            .. witnesses
                .SelectMany(w => w.Shard.Id == owner.Id
                    ? new[] { $"Patient/{w.Id}", Absolute(w) }
                    : [Absolute(w)])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
        return Build(plan, owner, string.Join(',', forms));

        static string Absolute((ShardDescriptor Shard, string Id) w) => $"{w.Shard.BaseUrl.ToString().TrimEnd('/')}/Patient/{w.Id}";
    }

    private static ShardSearchRequest Build(QueryPlan plan, ShardDescriptor owner, string decoded)
    {
        PlannedOccurrence chain = plan.Occurrences.Single(o => o.Kind == OccurrenceKind.ForwardChain);
        IReadOnlyList<ParameterOccurrence> parameters = plan.BuildBackendParameters(new Dictionary<int, string> { [chain.Occurrence.Ordinal] = decoded });
        return new ShardSearchRequest(owner.Id, plan.Type, parameters, plan.PageSize!.Value,
            owner.Profile.SupportsPostSearch ? ShardSearchMethod.PostSearch : ShardSearchMethod.Get);
    }

    /// <summary>A pure rebuild: the same owner rewrites recovered from the saved streams, or <see langword="null"/> when they are not the registry's.</summary>
    private static List<ShardSearchRequest>? Faithful(ForwardRebuildCall call)
    {
        IReadOnlyList<ShardDescriptor> owners = call.Capabilities.Registry.Shards;
        PlannedOccurrence[] outbound = [.. call.Plan.Occurrences.Where(o => o.Action != OccurrenceAction.Remove)];
        int at = Array.FindIndex(outbound, o => o.Kind == OccurrenceKind.ForwardChain);
        if (at < 0 || call.Saved.Count != owners.Count)
        {
            return null;
        }

        var streams = new List<ShardSearchRequest>();
        for (int i = 0; i < owners.Count; i++)
        {
            if (call.Saved[i].Shard != owners[i].Id || call.Saved[i].Parameters.Count != outbound.Length)
            {
                return null;
            }

            streams.Add(Build(call.Plan, owners[i], WebUtility.UrlDecode(call.Saved[i].Parameters[at].Value)));
        }

        return streams;
    }
}

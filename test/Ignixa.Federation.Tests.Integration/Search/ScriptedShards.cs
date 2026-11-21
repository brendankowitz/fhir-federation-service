using System.Collections.Concurrent;
using System.Globalization;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>
/// A scripted shard transport over the collision corpus. Each shard returns the selected seeds in file order,
/// <c>_count</c> at a time, with opaque <c>FollowGet</c> cursors. Like a real transport it charges the
/// request budget before every send and for every body chunk it reads, and it records what it was asked.
/// </summary>
internal sealed class ScriptedShards : IShardClientFactory
{
    private readonly ConcurrentQueue<ShardCall> _calls = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets every call that reached the transport, in arrival order.</summary>
    public IReadOnlyList<ShardCall> Calls => [.. _calls];

    /// <summary>Gets the view each client was created for.</summary>
    public ConcurrentQueue<SearchCapabilities> Views { get; } = new();

    /// <summary>Gets or sets how many physical sends each call makes (more than one models a retry).</summary>
    public int SendsPerCall { get; set; } = 1;

    /// <summary>Gets or sets the maximum entries the scripted backend serves per page.</summary>
    public int PageSizeLimit { get; set; } = int.MaxValue;

    /// <summary>Gets or sets the shard whose calls fail with a transport error.</summary>
    public string? FailingShard { get; set; }

    /// <summary>Gets or sets the shard whose calls return an entry of the wrong type.</summary>
    public string? MalformedShard { get; set; }

    /// <summary>
    /// Gets or sets the shard whose entries keep a valid key but whose raw JSON is cut short, so only the searchset
    /// writer can reject them.
    /// </summary>
    public string? UnshapeableShard { get; set; }

    /// <summary>Gets or sets a value indicating whether calls wait for cancellation instead of answering.</summary>
    public bool Hang { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether count-only searches (a search with page size zero, not a cursor follow) wait after
    /// being charged, until cancelled or <see cref="ReleaseParked"/>. Every other call, such as discovery, answers normally.
    /// </summary>
    public bool ParkCounts { get; set; }

    /// <summary>Lets every parked count call continue, so a test can always tear its host down. Safe to call more than once.</summary>
    public void ReleaseParked() => _released.TrySetResult();

    /// <summary>Gets a signal set when a hanging call has started waiting.</summary>
    public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets a signal set when a hanging call observed cancellation.</summary>
    public TaskCompletionSource ObservedCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets or sets an action run after the budget is charged and before the answer (for example, moving a clock).</summary>
    public Action? DuringCall { get; set; }

    /// <summary>Gets or sets an action run once a page has been answered, as it is returned (for example, letting a deadline pass after the response).</summary>
    public Action<RequestBudget>? Answered { get; set; }

    /// <summary>Supplies a backend response for a count-only search, when scripted.</summary>
    public Func<ShardSearchRequest, ShardPage>? CountAnswer { get; set; }

    public bool FilterGeneratedIds { get; set; }

    public IReadOnlyDictionary<string, IReadOnlyList<SearchWorld.SeedResource>> Resources { get; set; } = SearchWorld.Resources;

    public Func<ShardSearchRequest, bool>? RequestFits { get; set; }

    public bool FitsSearchRequest(ShardSearchRequest request) => RequestFits?.Invoke(request) ?? true;

    public IShardClient CreateClient(SearchCapabilities view, RequestBudget budget)
    {
        Views.Enqueue(view);
        return new Client(this, view.Registry, budget);
    }

    private sealed class Client(ScriptedShards script, ShardRegistry registry, RequestBudget budget) : IShardClient
    {
        public async Task<ShardPage> SearchAsync(ShardSearchRequest request, CancellationToken cancellationToken)
        {
            script._calls.Enqueue(new ShardCall(
                request.Shard.Value,
                request.Method,
                string.Join('&', request.Parameters.Select(p => $"{p.Key}={p.Value}")),
                request.PageSize,
                Target: null));
            await SendAsync(request.Shard, cancellationToken, parkable: request.PageSize == 0);
            if (request.PageSize == 0 && script.CountAnswer is { } count)
            {
                return Answered(count(request));
            }

            ShardDescriptor shard = Shard(request.Shard);
            return Answered(Answer(shard, request.Type, 0, Math.Min(request.PageSize, script.PageSizeLimit),
                script.FilterGeneratedIds ? request.Parameters.FirstOrDefault(p => p.Key == "_id")?.Value : null,
                target => ShardCursor.Next(request, shard.Profile, target)));
        }

        public async Task<ShardPage> ContinueAsync(ShardCursor cursor, CancellationToken cancellationToken)
        {
            script._calls.Enqueue(new ShardCall(cursor.Shard.Value, Method: null, Parameters: null, PageSize: 0, cursor.Target));
            await SendAsync(cursor.Shard, cancellationToken);
            var query = new Uri(cursor.Target).Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            int offset = int.Parse(query["_offset"], CultureInfo.InvariantCulture);
            int count = int.Parse(query["_count"], CultureInfo.InvariantCulture);
            return Answered(Answer(Shard(cursor.Shard), cursor.Type, offset, count, query.GetValueOrDefault("_id"),
                target => new ShardCursor(cursor.Shard, cursor.Type, target, cursor.Redemption)));
        }

        private ShardPage Answered(ShardPage page)
        {
            script.Answered?.Invoke(budget);
            return page;
        }

        private ShardDescriptor Shard(ShardId id) =>
            registry.TryGetById(id, out ShardDescriptor? shard) ? shard : throw new InvalidOperationException("Unknown shard.");

        private async Task SendAsync(ShardId shard, CancellationToken cancellationToken, bool parkable = false)
        {
            for (int i = 0; i < script.SendsPerCall; i++)
            {
                budget.BeforeBackendCall();
            }

            script.DuringCall?.Invoke();
            if (script.Hang || (parkable && script.ParkCounts))
            {
                script.Hanging.TrySetResult();
                try
                {
                    await (script.Hang ? Task.Delay(Timeout.Infinite, cancellationToken) : script._released.Task.WaitAsync(cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    script.ObservedCancellation.TrySetResult();
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (shard.Value == script.FailingShard)
            {
                throw new HttpRequestException("scripted transport failure at https://fhir-west.example.org/secret-path");
            }
        }

        private ShardPage Answer(ShardDescriptor shard, ResourceType type, int offset, int count, string? ids, Func<string, ShardCursor> cursor)
        {
            string[]? selected = ids is null ? null : Uri.UnescapeDataString(ids).Split(',');
            SearchWorld.SeedResource[] all = [.. script.Resources[shard.Id.Value]
                .Where(r => r.Type == type.Value && (selected is null || selected.Contains(r.Id, StringComparer.Ordinal)))];
            var entries = new List<ShardEntry>();
            foreach (SearchWorld.SeedResource resource in all.Skip(offset).Take(count))
            {
                budget.OnResponseBytesConsumed(resource.Json.Length);
                ResourceType entryType = shard.Id.Value == script.MalformedShard ? ResourceType.Parse("Observation") : type;
                byte[] json = shard.Id.Value == script.UnshapeableShard ? resource.Json[..^1] : resource.Json;
                entries.Add(new ShardEntry(new ResourceKey(shard.Id, entryType, ResourceId.Parse(resource.Id)), null, null, json));
            }

            int end = offset + count;
            ShardCursor? next = end < all.Length
                ? cursor($"{shard.BaseUrl}{type.Value}?_getpages=opaque-{shard.Id.Value}&_offset={end}&_count={count}"
                    + (ids is null ? string.Empty : $"&_id={ids}"))
                : null;
            return new ShardPage(entries, next, null);
        }
    }
}

/// <summary>One call that reached the scripted transport: a search (with its method and parameters) or a cursor follow.</summary>
internal sealed record ShardCall(string Shard, ShardSearchMethod? Method, string? Parameters, int PageSize, string? Target);

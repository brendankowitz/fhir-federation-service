using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// One synthetic shard at a <see cref="LoopbackFhirServer"/>, with a client that trusts only the generated loopback leaf.
/// The shard has link base alias <see cref="AliasBase"/>, which the client must accept but never connect to.
/// </summary>
internal sealed class ShardHarness : IAsyncDisposable
{
    public const string ShardAlias = "k7q2";

    public static readonly BackendProfile Repost = new("SyntheticRepost", SupportsPostSearch: true, MaxCount: 50, NextLinkRedemption.RepostToSearch, null);

    public static readonly BackendProfile Follow = new("SyntheticFollow", SupportsPostSearch: true, MaxCount: 50, NextLinkRedemption.FollowGet, null);

    public static readonly BackendProfile GetOnly = new("SyntheticGetOnly", SupportsPostSearch: false, MaxCount: 50, NextLinkRedemption.FollowGet, null);

    public static readonly ResourceType Patient = ResourceType.Parse("Patient");

    private readonly List<HttpShardClient> _extraClients = [];
    private readonly TimeSpan _callTimeout;
    private readonly long _maxResponseBytes;

    private ShardHarness(string id, BackendProfile profile, TimeSpan callTimeout, long maxResponseBytes, bool trustLoopback, string? basePath, TimeProvider? clock)
    {
        _callTimeout = callTimeout;
        _maxResponseBytes = maxResponseBytes;
        Server = new LoopbackFhirServer();
        var options = new ShardOptions
        {
            Id = id,
            Alias = ShardAlias,
            BaseUrl = basePath is null ? Server.BaseUrl : Server.Origin + basePath,
            Profile = profile.Name,
            Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir.synthetic.example" },
        };
        options.LinkBaseAliases.Add(AliasBase);
        Shard = ShardRegistry.Create([options], [Repost, Follow, GetOnly]).Shards.Single();
        Resilience = new ShardResilience(Shard, clock);
        Client = trustLoopback
            ? new HttpShardClient(Shard, Credentials, callTimeout, maxResponseBytes, Meter, Resilience, LoopbackCertificate.Trust)
            : new HttpShardClient(Shard, Credentials, callTimeout, maxResponseBytes, Meter, Resilience);
    }

    public LoopbackFhirServer Server { get; }

    public SyntheticCredentials Credentials { get; } = new();

    public RecordingMeter Meter { get; } = new();

    public ShardDescriptor Shard { get; }

    /// <summary>Gets the breaker state the harness's clients share.</summary>
    public ShardResilience Resilience { get; }

    public HttpShardClient Client { get; }

    /// <summary>Gets the typed search URL at the configured base.</summary>
    public string Search => Server.BaseUrl + "/Patient";

    /// <summary>Gets a configured link base alias: the same server under another name the client never connects to.</summary>
    public string AliasBase => $"https://localhost:{Server.Port}/fhir";

    public static ShardHarness Start(
        BackendProfile? profile = null,
        TimeSpan? callTimeout = null,
        long maxResponseBytes = 1 << 20,
        bool trustLoopback = true,
        string id = "east",
        string? basePath = null,
        TimeProvider? clock = null)
    {
        return new ShardHarness(id, profile ?? Repost, callTimeout ?? TimeSpan.FromSeconds(10), maxResponseBytes, trustLoopback, basePath, clock);
    }

    /// <summary>Another client for the same shard that shares this harness's breaker state but reports to its own meter.</summary>
    public HttpShardClient AddClient(RecordingMeter meter)
    {
        var client = new HttpShardClient(Shard, Credentials, _callTimeout, _maxResponseBytes, meter, Resilience, LoopbackCertificate.Trust);
        _extraClients.Add(client);
        return client;
    }

    /// <summary>A Patient search; parameters alternate raw key and raw value.</summary>
    public ShardSearchRequest Request(ShardSearchMethod method, int pageSize, params string[] keysAndValues)
    {
        var occurrences = new List<ParameterOccurrence>();
        for (int i = 0; i < keysAndValues.Length; i += 2)
        {
            occurrences.Add(new ParameterOccurrence(keysAndValues[i], keysAndValues[i + 1]));
        }

        return new ShardSearchRequest(Shard.Id, Patient, occurrences, pageSize, method);
    }

    /// <summary>A GET search for <c>name=Sarah</c>, page size 10: sent as <c>name=Sarah&amp;_count=10</c>.</summary>
    public ShardSearchRequest SarahRequest(ShardSearchMethod method = ShardSearchMethod.Get) => Request(method, 10, "name", "Sarah");

    /// <summary>A first page that passes the self check for <see cref="SarahRequest"/>.</summary>
    public string SarahPage(string? next = null, long? total = null, params string[] entries) =>
        Searchsets.Page(Search + "?name=Sarah&_count=10", next, total, entries);

    public ShardCursor Cursor(string target, NextLinkRedemption redemption = NextLinkRedemption.FollowGet) =>
        new(Shard.Id, Patient, target, redemption);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        foreach (HttpShardClient extra in _extraClients)
        {
            extra.Dispose();
        }
        await Server.DisposeAsync();
    }
}

namespace Ignixa.Federation.Core.Configuration;

/// <summary>The <c>Federation:Limits</c> section: page sizes and per-request cost caps. Every value must be positive.</summary>
public sealed class LimitOptions
{
    /// <summary>Gets or sets the page size when the caller sends no <c>_count</c> (design spec §7.3). Default 20.</summary>
    public int DefaultPageSize { get; set; } = 20;

    /// <summary>Gets or sets the largest page size; at most 100 (design spec §7.3). Default 100.</summary>
    public int MaxPageSize { get; set; } = 100;

    /// <summary>Gets or sets the most distinct keys a chain or <c>_has</c> may resolve (design spec §7.5). Default 1000.</summary>
    public int MaxChainKeys { get; set; } = 1000;

    /// <summary>Gets or sets the most source rows a reverse chain may scan (design spec §7.5). Default 5000.</summary>
    public int MaxChainSourceRows { get; set; } = 5000;

    /// <summary>Gets or sets the most included resources per page (design spec §7.6). Default 1000.</summary>
    public int MaxIncludesPerPage { get; set; } = 1000;

    /// <summary>Gets or sets the backend-call budget per shard; a request may make this times the shard count (design spec §7.8). Default 40.</summary>
    public int BackendCallsPerShard { get; set; } = 40;

    /// <summary>Gets or sets the most backend response bytes one request may read (design spec §7.8). Default 64 MiB.</summary>
    public long MaxResponseBytes { get; set; } = 67_108_864;

    /// <summary>Gets or sets the most concurrent backend calls per request (design spec §7.1). Default 16.</summary>
    public int MaxParallelBackendCalls { get; set; } = 16;

    /// <summary>Gets or sets the shard count above which startup warns; a soft limit, not a cap (design spec §3, D6). Default 16.</summary>
    public int ShardCountWarningThreshold { get; set; } = 16;
}

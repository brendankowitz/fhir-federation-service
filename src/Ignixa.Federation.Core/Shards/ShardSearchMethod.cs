namespace Ignixa.Federation.Core.Shards;

/// <summary>How a shard search is sent to its backend (design spec §7).</summary>
public enum ShardSearchMethod
{
    /// <summary><c>GET [base]/[type]?params</c>.</summary>
    Get,

    /// <summary><c>POST [base]/[type]/_search</c> with a form-encoded body, for queries too long for a URL.</summary>
    PostSearch,
}

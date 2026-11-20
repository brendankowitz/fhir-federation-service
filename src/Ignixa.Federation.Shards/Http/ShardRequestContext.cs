namespace Ignixa.Federation.Shards.Http;

internal readonly struct ShardRequestContext(IShardTransferMeter meter, string? callerId)
{
    public const string CallerHeader = "X-Federation-Caller";

    public IShardTransferMeter? Meter { get; } = meter;

    public string? CallerId { get; } = callerId;
}

using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>Hands out a fixed synthetic token and counts acquisitions, so tests can prove when a credential was not used.</summary>
internal sealed class SyntheticCredentials : IShardCredentialProvider
{
    public const string Token = "synthetic-token";

    private int _calls;

    /// <summary>Gets or sets what the next acquisitions return or do; by default they return <see cref="Token"/>.</summary>
    public Func<CancellationToken, ValueTask<string>> Acquire { get; set; } = _ => ValueTask.FromResult(Token);

    public int Calls => Volatile.Read(ref _calls);

    public ValueTask<string> GetAccessTokenAsync(ShardAuth auth, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Acquire(cancellationToken);
    }
}

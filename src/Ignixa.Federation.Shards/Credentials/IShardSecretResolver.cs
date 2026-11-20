namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Supplies the client secret a <c>ClientCredentials</c> shard names (design spec §10.2): the composition boundary for the
/// deployer's secret store.
/// </summary>
/// <remarks>
/// <para><b>Freshness.</b> <see cref="AzureShardCredentialProvider"/> calls this once for every token acquisition and keeps
/// nothing, so the implementation owns any caching and must return a rotated secret as soon as its store does.</para>
/// <para><b>Failure contract.</b> Report a secret that is missing, unreadable or unauthorised by throwing
/// <see cref="ShardCredentialException"/>, whose message is fixed text. The provider also converts the exceptions a secret
/// store client normally raises (Azure SDK request and authentication failures, <see cref="HttpRequestException"/>,
/// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>), and a canceled token. Any other exception is a
/// contract violation: it fails the acquisition but reaches the caller unchanged, so its message must not hold a secret.</para>
/// </remarks>
public interface IShardSecretResolver
{
    /// <summary>Gets the current value of a named secret.</summary>
    /// <param name="secretName">The name configured for the shard; never the secret.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The secret value, not empty.</returns>
    ValueTask<string> ResolveAsync(string secretName, CancellationToken cancellationToken);
}

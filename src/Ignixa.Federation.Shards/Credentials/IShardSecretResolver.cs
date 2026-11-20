namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Supplies the client secret a <c>ClientCredentials</c> shard names (design spec §10.2): the composition boundary for the
/// deployer's secret store.
/// </summary>
/// <remarks>
/// <para><b>Freshness.</b> <see cref="AzureShardCredentialProvider"/> calls this once for every client-secret token
/// acquisition, so the implementation owns any caching and must return a rotated secret as soon as its store does. The
/// provider keeps no copy of the value outside the SDK credential built from it and the acquisition in progress; it
/// recognises an unchanged value by a non-logged fingerprint. A different value returned by a lookup that started before
/// one already accepted is treated as stale and fails closed, so an implementation should not serve older values after
/// newer ones.</para>
/// <para><b>Failure contract.</b> Report an expected failure (a secret that is missing, unreadable or unauthorised) by
/// throwing <see cref="ShardCredentialException"/>. The provider never forwards that object: it throws a fresh
/// <see cref="ShardCredentialFailure.SecretUnavailable"/> and drops the supplied failure kind, <c>Source</c>, <c>Data</c> and
/// inner exceptions. It applies the same replacement to a short explicit list of exceptions a secret store client normally
/// raises: Azure SDK request and authentication failures, <see cref="HttpRequestException"/>, <see cref="IOException"/>,
/// <see cref="UnauthorizedAccessException"/>, <see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>,
/// <see cref="KeyNotFoundException"/>, <see cref="TimeoutException"/> and <see cref="AggregateException"/>. Once the caller's
/// token is canceled, any of these, or a cancellation, surfaces only as a cancellation. Nothing else is sanitized: any other
/// exception is a bug in the resolver and reaches the caller unchanged, so an implementation must not put a secret or
/// other secret-bearing diagnostics in any exception it can throw.</para>
/// <para><b>Cancellation.</b> Honor the cancellation token. A value returned after it was canceled is discarded and never
/// used.</para>
/// </remarks>
public interface IShardSecretResolver
{
    /// <summary>Gets the current value of a named secret.</summary>
    /// <param name="secretName">The name configured for the shard; never the secret.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The secret value, not empty.</returns>
    ValueTask<string> ResolveAsync(string secretName, CancellationToken cancellationToken);
}

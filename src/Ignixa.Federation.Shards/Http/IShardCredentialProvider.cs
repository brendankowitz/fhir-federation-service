using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.Http;

/// <summary>Acquires the bearer token <see cref="HttpShardClient"/> sends to a shard (design spec §10.2).</summary>
/// <remarks>
/// <para>The client calls this once per backend call, after the request and any cursor target have been validated, so a
/// token is only acquired for a call that goes to the shard's own base URL. Implementations own caching and refresh.</para>
/// <para>Failures propagate unchanged, so an implementation must keep secrets out of its exception messages. The token
/// passed in is linked to the per-call deadline: a call that outlasts it fails as
/// <see cref="ShardCallFailure.Timeout"/>.</para>
/// <para>The client invokes this on the thread pool and can stop waiting before the provider returns. Abandoned
/// acquisitions still count toward the client's concurrency limit, so providers should honor cancellation.</para>
/// </remarks>
public interface IShardCredentialProvider
{
    /// <summary>Gets an access token for the shard's configured authentication.</summary>
    /// <param name="auth">The shard's authentication settings.</param>
    /// <param name="cancellationToken">Cancels the acquisition; fires on caller cancellation or the call deadline.</param>
    /// <returns>The token, as RFC 6750 <c>b64token</c> text without the <c>Bearer</c> scheme.</returns>
    ValueTask<string> GetAccessTokenAsync(ShardAuth auth, CancellationToken cancellationToken);
}

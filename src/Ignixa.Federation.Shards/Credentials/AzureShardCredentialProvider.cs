using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Identity;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Http;

namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Acquires the bearer token for one outgoing shard call from the shard's published <see cref="ShardAuth"/> with the Azure
/// SDK (design spec §10.2). There is no caller identity, developer sign-in, credential chain or environment lookup.
/// </summary>
/// <remarks>
/// <para><b>Variants.</b> <see cref="ManagedIdentityAuth"/> uses a <see cref="ManagedIdentityCredential"/> for exactly the
/// system-assigned identity or the user-assigned client id. <see cref="ClientCredentialsAuth"/> uses a
/// <see cref="ClientSecretCredential"/> for exactly its tenant and client, with the secret from the
/// <see cref="IShardSecretResolver"/>. Both are pinned to the Azure public cloud authority with SDK logging and tracing off.</para>
/// <para><b>Scope.</b> The configured resource is an audience; the one requested scope is that text followed by
/// <c>/.default</c>, spelled exactly as configured. A trailing slash is kept: it can be part of the audience, and the
/// double slash that results is what Azure documents for such an audience. The only validation is that the resource holds
/// no whitespace or control character and does not already end in <c>/.default</c>.</para>
/// <para><b>Caching.</b> A client-secret credential is built for every acquisition from a freshly resolved secret, so a
/// rotated secret takes effect on the next call and no secret or client token is kept here; the cost is one token request
/// per call. A managed identity credential holds no secret, so one is kept per identity (a bounded set: the identities in
/// the registry) and the SDK caches its tokens per scope.</para>
/// <para><b>Failures.</b> Every failure is fail-closed: no request is sent. See <see cref="ShardCredentialException"/>.
/// Caller cancellation surfaces as an <see cref="OperationCanceledException"/> for the given token. Thread-safe.</para>
/// </remarks>
public sealed class AzureShardCredentialProvider : IShardCredentialProvider
{
    private const string DefaultScopeSuffix = "/.default";

    private readonly IShardSecretResolver _secrets;
    private readonly Func<string?, TokenCredential> _managedIdentity;
    private readonly Func<string, string, string, TokenCredential> _clientSecret;
    private readonly ConcurrentDictionary<string, TokenCredential> _managedCredentials = new(StringComparer.Ordinal);

    /// <summary>Creates the provider with the production Azure credentials.</summary>
    /// <param name="secrets">Supplies client secrets by name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="secrets"/> is <see langword="null"/>.</exception>
    public AzureShardCredentialProvider(IShardSecretResolver secrets)
        : this(secrets, AzureCredentialFactory.ManagedIdentity, AzureCredentialFactory.ClientSecret)
    {
    }

    // The SDK seam: the tests substitute credential construction so no identity service is contacted.
    internal AzureShardCredentialProvider(
        IShardSecretResolver secrets,
        Func<string?, TokenCredential> managedIdentity,
        Func<string, string, string, TokenCredential> clientSecret)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _secrets = secrets;
        _managedIdentity = managedIdentity;
        _clientSecret = clientSecret;
    }

    /// <inheritdoc />
    /// <exception cref="ShardCredentialException">The token could not be acquired; the message is fixed and sanitized.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<string> GetAccessTokenAsync(ShardAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await AcquireAsync(auth, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A fresh exception: the one caught may carry SDK text.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.TimedOut);
        }
    }

    private static string ScopeFor(string resource)
    {
        foreach (char c in resource)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);
            }
        }

        if (resource.EndsWith(DefaultScopeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);
        }

        return resource + DefaultScopeSuffix;
    }

    private async ValueTask<string> AcquireAsync(ShardAuth auth, CancellationToken cancellationToken)
    {
        string scope;
        TokenCredential credential;
        switch (auth)
        {
            case ManagedIdentityAuth managed:
                scope = ScopeFor(managed.Resource);
                credential = ManagedCredential(managed.ClientId);
                break;
            case ClientCredentialsAuth client:
                scope = ScopeFor(client.Resource);
                string secret = await ResolveSecretAsync(client.SecretName, cancellationToken).ConfigureAwait(false);
                credential = ClientCredential(client, secret);
                break;
            default:
                throw new ArgumentException("Unsupported shard authentication.", nameof(auth));
        }

        AccessToken token = await RequestTokenAsync(credential, scope, cancellationToken).ConfigureAwait(false);

        // A token that arrived after the caller gave up is not returned.
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token.Token) || token.Token.AsSpan().ContainsAnyExceptInRange('!', '~'))
        {
            // The caller's own bearer check is the strict one; this only refuses text that cannot be a token at all.
            throw new ShardCredentialException(ShardCredentialFailure.InvalidToken);
        }

        return token.Token;
    }

    private TokenCredential ManagedCredential(string? clientId)
    {
        // A client id is never empty (the registry maps blank to null), so the empty key is the system-assigned identity.
        try
        {
            return _managedCredentials.GetOrAdd(clientId ?? string.Empty, _ => _managedIdentity(clientId));
        }
        catch (ArgumentException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);
        }
    }

    private TokenCredential ClientCredential(ClientCredentialsAuth auth, string secret)
    {
        try
        {
            return _clientSecret(auth.TenantId, auth.ClientId, secret);
        }
        catch (ArgumentException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);
        }
    }

    // The resolver is an external boundary. Its contract (IShardSecretResolver) is the typed ShardCredentialException, which
    // already has a fixed message and passes through. The classes below are what secret store clients normally raise; any
    // other exception breaks the contract and propagates, because nothing safe can be said about it.
    private async ValueTask<string> ResolveSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = await _secrets.ResolveAsync(secretName, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }
        catch (AuthenticationFailedException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }
        catch (HttpRequestException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }
        catch (IOException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }

        return secret;
    }

    private static async ValueTask<AccessToken> RequestTokenAsync(TokenCredential credential, string scope, CancellationToken cancellationToken)
    {
        try
        {
            return await credential.GetTokenAsync(new TokenRequestContext([scope]), cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationFailedException)
        {
            // Includes CredentialUnavailableException.
            throw new ShardCredentialException(ShardCredentialFailure.TokenUnavailable);
        }
        catch (RequestFailedException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.TokenUnavailable);
        }
    }
}

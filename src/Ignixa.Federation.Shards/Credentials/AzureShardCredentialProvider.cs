using System.Buffers;
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
/// system-assigned identity or the user-assigned client id; the identity is whatever the deployed platform (App Service,
/// Container Apps, AKS, a VM) supplies through its own managed identity endpoint, and nothing here selects a cloud or
/// authority. <see cref="ClientCredentialsAuth"/> uses a <see cref="ClientSecretCredential"/> for exactly its tenant and
/// client, with the secret from the <see cref="IShardSecretResolver"/>, bound to the Azure public cloud authority. Both
/// have SDK logging and tracing off. Only the Azure public cloud is supported for client secrets: there is no sovereign,
/// government or custom authority.</para>
/// <para><b>Scope.</b> The configured resource is an audience; the one requested scope is that text followed by
/// <c>/.default</c>, spelled exactly as configured. A trailing slash is kept: it can be part of the audience, and the
/// double slash that results is what Azure documents for such an audience. The only validation is that the resource holds
/// no whitespace or control character and does not already end in <c>/.default</c>. It is not required to be a URI,
/// because an Entra audience may be an application id GUID or an <c>api://</c> identifier, and no other URI rule has been
/// shown to be needed.</para>
/// <para><b>Caching and refresh.</b> This type caches no token. It keeps SDK credentials, and each SDK credential caches
/// its tokens per scope and refreshes one that is expired or near expiry. A managed identity credential is kept per
/// identity. A client-secret credential is kept per tenant, client and secret name; both sets are bounded by the
/// registry.</para>
/// <para><b>Rotation and freshness.</b> Every client-secret acquisition resolves the named secret afresh. An unchanged
/// value, recognised by a non-logged SHA-256 fingerprint, reuses the kept credential and its token cache; a changed value
/// replaces it, so the next acquisition after a rotation exchanges the new secret. Acquisitions already under way finish
/// with the credential they selected. A lookup that fails, is blank or is canceled never uses or replaces the kept
/// credential. A lookup that returns a different value after a later-started lookup was accepted is stale and fails as
/// <see cref="ShardCredentialFailure.SecretUnavailable"/> rather than rolling the rotation back.</para>
/// <para><b>Failures.</b> Every failure is fail-closed: no request is sent. See <see cref="ShardCredentialException"/>.
/// Caller cancellation surfaces as an <see cref="OperationCanceledException"/> for the given token. Thread-safe.</para>
/// </remarks>
public sealed class AzureShardCredentialProvider : IShardCredentialProvider
{
    private const string DefaultScopeSuffix = "/.default";

    private static readonly SearchValues<char> B64TokenCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~+/");

    private readonly IShardSecretResolver _secrets;
    private readonly Func<string?, TokenCredential> _managedIdentity;
    private readonly ClientSecretCredentialCache _clientCredentials;
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
        _clientCredentials = new ClientSecretCredentialCache(clientSecret);
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
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ShardCredentialException)
        {
            // The caller gave up, so whatever the SDK or the resolver reported is moot. A fresh exception: the one caught
            // may carry SDK text.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.TimedOut);
        }
    }

    // The pure lexical rule behind the scope; the prototype composition applies it at registration.
    internal static bool IsValidResource(string resource)
    {
        foreach (char c in resource)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        return !resource.EndsWith(DefaultScopeSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ScopeFor(string resource) =>
        IsValidResource(resource)
            ? resource + DefaultScopeSuffix
            : throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);

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
                long lookup = _clientCredentials.BeginLookup();
                string secret = await ResolveSecretAsync(client.SecretName, cancellationToken).ConfigureAwait(false);
                credential = ClientCredential(client, lookup, secret, cancellationToken);
                break;
            default:
                throw new ArgumentException("Unsupported shard authentication.", nameof(auth));
        }

        AccessToken token = await RequestTokenAsync(credential, scope, cancellationToken).ConfigureAwait(false);

        // A token that arrived after the caller gave up is not returned.
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsB64Token(token.Token))
        {
            // Only the RFC 6750 b64token grammar can be a bearer credential; the text is never quoted.
            throw new ShardCredentialException(ShardCredentialFailure.InvalidToken);
        }

        return token.Token;
    }

    // RFC 6750 b64token = 1*( ALPHA / DIGIT / "-" / "." / "_" / "~" / "+" / "/" ) *"=".
    private static bool IsB64Token(string? token)
    {
        ReadOnlySpan<char> body = token.AsSpan().TrimEnd('=');
        return !body.IsEmpty && !body.ContainsAnyExcept(B64TokenCharacters);
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

    private TokenCredential ClientCredential(ClientCredentialsAuth auth, long lookup, string secret, CancellationToken cancellationToken)
    {
        try
        {
            return _clientCredentials.Select(auth, lookup, secret, cancellationToken);
        }
        catch (ArgumentException)
        {
            throw new ShardCredentialException(ShardCredentialFailure.InvalidConfiguration);
        }
    }

    // The resolver is an external boundary. Its contract (IShardSecretResolver) is the typed ShardCredentialException, and a
    // secret store client normally raises the other types listed in IsKnownStoreFailure. Each is replaced by a fresh
    // SecretUnavailable: the original object, with its message, inner exception, Data and Source, is never forwarded. Any other
    // exception breaks the contract and propagates unchanged, because nothing safe can be said about it.
    private async ValueTask<string> ResolveSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        string secret;
        try
        {
            secret = await _secrets.ResolveAsync(secretName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsKnownStoreFailure(ex))
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }

        // A secret that arrives after the caller gave up must not reach the SDK.
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }

        return secret;
    }

    // An explicit allow-list, not a catch-all. Cancellation is deliberately absent: it is handled by the caller's token.
    private static bool IsKnownStoreFailure(Exception ex) =>
        ex is ShardCredentialException
            or RequestFailedException
            or AuthenticationFailedException
            or HttpRequestException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or KeyNotFoundException
            or TimeoutException
            or AggregateException;

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

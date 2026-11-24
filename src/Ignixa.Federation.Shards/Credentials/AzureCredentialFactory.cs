using Azure.Core;
using Azure.Identity;

namespace Ignixa.Federation.Shards.Credentials;

/// <summary>
/// Builds the real Azure SDK credentials. Each is pinned to the Azure public cloud authority, so neither the
/// <c>AZURE_AUTHORITY_HOST</c> environment variable nor a default chain can pick another, and the SDK's request logging,
/// content logging, account-identifier logging, unsafe support logging and distributed tracing are all off.
/// </summary>
internal static class AzureCredentialFactory
{
    /// <summary>Builds a managed identity credential: the system-assigned identity, or the user-assigned one with this client id.</summary>
    internal static TokenCredential ManagedIdentity(string? clientId) => new ManagedIdentityCredential(ManagedIdentityOptions(clientId));

    /// <summary>Builds a client-secret credential for the client-credentials flow in the given tenant.</summary>
    /// <exception cref="ArgumentException">The SDK rejects the tenant id or client id.</exception>
    internal static TokenCredential ClientSecret(string tenantId, string clientId, string secret) =>
        new ClientSecretCredential(tenantId, clientId, secret, ClientSecretOptions());

    internal static ClientSecretCredentialOptions ClientSecretOptions() => Pin(new ClientSecretCredentialOptions());

    internal static ManagedIdentityCredentialOptions ManagedIdentityOptions(string? clientId) =>
        Pin(new ManagedIdentityCredentialOptions(
            clientId is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(clientId)));

    private static T Pin<T>(T options)
        where T : TokenCredentialOptions
    {
        options.AuthorityHost = AzureAuthorityHosts.AzurePublicCloud;
        options.IsUnsafeSupportLoggingEnabled = false;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsLoggingContentEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        options.Diagnostics.IsAccountIdentifierLoggingEnabled = false;
        return options;
    }
}

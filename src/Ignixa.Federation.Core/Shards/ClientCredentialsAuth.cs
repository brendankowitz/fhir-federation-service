namespace Ignixa.Federation.Core.Shards;

/// <summary>
/// An Entra app registration using the client-credentials flow (design spec §10.2). The client secret is never held
/// here: <see cref="SecretName"/> names where it is stored.
/// </summary>
public sealed record ClientCredentialsAuth : ShardAuth
{
    internal ClientCredentialsAuth(string tenantId, string clientId, string resource, string secretName)
    {
        TenantId = tenantId;
        ClientId = clientId;
        Resource = resource;
        SecretName = secretName;
    }

    /// <summary>Gets the Entra tenant id.</summary>
    public string TenantId { get; }

    /// <summary>Gets the client id of the app registration.</summary>
    public string ClientId { get; }

    /// <summary>Gets the token resource (audience) of the shard.</summary>
    public string Resource { get; }

    /// <summary>Gets the name of the secret holding the client secret; never the secret itself.</summary>
    public string SecretName { get; }
}

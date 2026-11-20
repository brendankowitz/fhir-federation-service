namespace Ignixa.Federation.Core.Configuration;

/// <summary>
/// The federation's read-only credential for one shard, as configured (design spec §10.2). There is deliberately no
/// secret property: <see cref="SecretName"/> names a secret held elsewhere.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Gets or sets the credential type: <c>ManagedIdentity</c> or <c>ClientCredentials</c> (design spec §10.2).</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the token resource (audience) of the shard. Required for both types (design spec §10.2).</summary>
    public string? Resource { get; set; }

    /// <summary>
    /// Gets or sets the client id: optional for <c>ManagedIdentity</c> (a user-assigned identity), required for
    /// <c>ClientCredentials</c> (design spec §10.2).
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the Entra tenant id. <c>ClientCredentials</c> only (design spec §10.2).</summary>
    public string? TenantId { get; set; }

    /// <summary>Gets or sets the name of the secret holding the client secret, never the secret itself. <c>ClientCredentials</c> only (design spec §10.2).</summary>
    public string? SecretName { get; set; }
}

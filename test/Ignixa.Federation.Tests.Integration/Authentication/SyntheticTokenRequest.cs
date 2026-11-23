namespace Ignixa.Federation.Tests.Integration.Authentication;

/// <summary>Describes one synthetic token. Defaults describe a valid token for the trusted issuer and audience.</summary>
internal sealed class SyntheticTokenRequest
{
    public string Issuer { get; init; } = SyntheticIdentityProvider.TrustedIssuer;

    public string Audience { get; init; } = SyntheticIdentityProvider.TrustedAudience;

    public DateTime NotBefore { get; init; } = DateTime.UtcNow.AddMinutes(-5);

    /// <summary>Gets the expiry, or <see langword="null"/> to issue a token without an <c>exp</c> claim.</summary>
    public DateTime? Expires { get; init; } = DateTime.UtcNow.AddMinutes(10);

    public string[]? Roles { get; init; }

    public string? Scp { get; init; }

    public string? Scope { get; init; }

    public string Subject { get; init; } = "synthetic-subject";

    /// <summary>Gets the Entra object id (<c>oid</c>) to emit, or <see langword="null"/> to omit it.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Gets the client id (<c>azp</c>) to emit, or <see langword="null"/> to omit it.</summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// Gets further claims to emit, written after the others and replacing any of the same exact name: one value as a
    /// string, more as an array.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? ExtraClaims { get; init; }

    /// <summary>Gets whether to sign with a key the identity provider does not publish (reusing the published key id).</summary>
    public bool UntrustedSigner { get; init; }
}

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

    /// <summary>Gets whether to sign with a key the identity provider does not publish (reusing the published key id).</summary>
    public bool UntrustedSigner { get; init; }
}

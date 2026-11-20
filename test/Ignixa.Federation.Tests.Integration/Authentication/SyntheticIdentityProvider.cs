using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ignixa.Federation.Tests.Integration.Authentication;

/// <summary>
/// A test-only, in-memory stand-in for an OIDC identity provider. It serves discovery metadata and a JWKS over an
/// <see cref="HttpMessageHandler"/> that never touches the network, and it signs synthetic tokens with an RSA key
/// generated at runtime. No credential, endpoint or key material from a real tenant is involved.
/// </summary>
internal sealed class SyntheticIdentityProvider : HttpMessageHandler
{
    public const string TrustedIssuer = "https://idp.example.test/tenant-a/v2.0";

    public const string TrustedAudience = "api://federation";

    public static readonly Uri Authority = new(TrustedIssuer);

    private const string KeyId = "synthetic-key-1";

    public static readonly Uri MetadataAddress = new($"{TrustedIssuer}/.well-known/openid-configuration");

    private readonly RSA _trustedKey = RSA.Create(2048);

    private readonly ConcurrentQueue<Uri> _requests = new();

    /// <summary>Gets or sets the issuer that the discovery document advertises; the tests use it to separate it from the pinned issuer.</summary>
    public string AdvertisedIssuer { get; set; } = TrustedIssuer;

    /// <summary>Gets or sets the JWKS address that the discovery document advertises.</summary>
    public Uri JwksAddress { get; set; } = new("https://idp.example.test/tenant-a/discovery/keys");

    public IReadOnlyList<Uri> Requests => _requests.ToArray();

    /// <summary>Signs a synthetic token. Defaults describe a valid token for the trusted issuer and audience.</summary>
    public string IssueToken(SyntheticTokenRequest? request = null)
    {
        request ??= new SyntheticTokenRequest();

        // The payload is written directly: a SecurityTokenDescriptor silently fills in an expiry when none is given.
        JsonObject payload = new()
        {
            ["iss"] = request.Issuer,
            ["aud"] = request.Audience,
            ["sub"] = request.Subject,
            ["nbf"] = new DateTimeOffset(request.NotBefore).ToUnixTimeSeconds(),
        };

        if (request.ObjectId is { } objectId)
        {
            payload["oid"] = objectId;
        }

        if (request.ClientId is { } clientId)
        {
            payload["azp"] = clientId;
        }

        if (request.Expires is { } expires)
        {
            payload["exp"] = new DateTimeOffset(expires).ToUnixTimeSeconds();
        }

        if (request.Roles is { } roles)
        {
            payload["roles"] = new JsonArray([.. roles.Select(r => JsonValue.Create(r))]);
        }

        if (request.Scp is { } scp)
        {
            payload["scp"] = scp;
        }

        if (request.Scope is { } scope)
        {
            payload["scope"] = scope;
        }

        foreach ((string name, string[] values) in request.ExtraClaims ?? new Dictionary<string, string[]>())
        {
            payload[name] = values is [string single] ? JsonValue.Create(single) : new JsonArray([.. values.Select(v => JsonValue.Create(v))]);
        }

        using RSA? untrustedSigner = request.UntrustedSigner ? RSA.Create(2048) : null;

        return new JsonWebTokenHandler().CreateToken(
            payload.ToJsonString(),
            new SigningCredentials(new RsaSecurityKey(untrustedSigner ?? _trustedKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256));
    }

    /// <summary>Builds a token whose header declares no signature algorithm and carries no signature.</summary>
    public static string IssueUnsignedToken()
    {
        long exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        string header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        string payload = Base64UrlEncoder.Encode(
            $$"""{"iss":"{{TrustedIssuer}}","aud":"{{TrustedAudience}}","sub":"synthetic-subject","exp":{{exp}}}""");

        return $"{header}.{payload}.";
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri address = request.RequestUri!;
        _requests.Enqueue(address);

        string? body = address == MetadataAddress ? DiscoveryDocument()
            : address == JwksAddress ? JwksDocument()
            : null;

        HttpResponseMessage response = body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _trustedKey.Dispose();
        }

        base.Dispose(disposing);
    }

    private string DiscoveryDocument() => $$"""
        {
          "issuer": "{{AdvertisedIssuer}}",
          "jwks_uri": "{{JwksAddress.AbsoluteUri}}",
          "authorization_endpoint": "https://idp.example.test/tenant-a/oauth2/v2.0/authorize",
          "token_endpoint": "https://idp.example.test/tenant-a/oauth2/v2.0/token",
          "response_types_supported": ["id_token"],
          "subject_types_supported": ["pairwise"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    private string JwksDocument()
    {
        RSAParameters parameters = _trustedKey.ExportParameters(includePrivateParameters: false);

        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = KeyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }
}

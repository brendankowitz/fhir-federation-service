using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Ignixa.Federation.Api.Authentication;

/// <summary>
/// Registers the federation-only authentication scheme that validates caller access tokens.
/// </summary>
/// <remarks>
/// The registration sets no default scheme options. Protected federation routes should select
/// <see cref="SchemeName"/> explicitly. Authorization, including SMART scope rejection, is separate.
/// </remarks>
internal static class FederationCallerAuthentication
{
    /// <summary>The name of the authentication scheme registered by <see cref="AddFederationCallerJwtBearer"/>.</summary>
    public const string SchemeName = "FederationCaller";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <summary>Registers the caller JWT bearer scheme under <see cref="SchemeName"/>.</summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="authority">The trusted HTTPS discovery authority, without user information, a query, or a fragment. Its metadata selects the HTTPS signing-key endpoint.</param>
    /// <param name="issuer">The one trusted issuer, matched exactly (ordinal) against the token's <c>iss</c>. The issuer advertised by the authority's metadata is deliberately not trusted in addition.</param>
    /// <param name="audience">The one trusted audience, matched exactly (ordinal, with no trailing-slash equivalence) against the token's <c>aud</c>.</param>
    /// <returns>The same <paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="authority"/> is not an absolute HTTPS address, or <paramref name="issuer"/> or <paramref name="audience"/> is blank.</exception>
    public static AuthenticationBuilder AddFederationCallerJwtBearer(this AuthenticationBuilder builder, Uri authority, string issuer, string audience)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        if (!authority.IsAbsoluteUri ||
            authority.Scheme != Uri.UriSchemeHttps ||
            authority.UserInfo.Length != 0 ||
            authority.Query.Length != 0 ||
            authority.Fragment.Length != 0)
        {
            throw new ArgumentException("The trusted authority must be an absolute HTTPS address without user information, a query, or a fragment.", nameof(authority));
        }

        return builder.AddJwtBearer(SchemeName, options =>
        {
            options.Authority = authority.AbsoluteUri;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.IncludeErrorDetails = false;

            TokenValidationParameters validation = options.TokenValidationParameters;
            validation.ValidateIssuer = true;

            // The handler appends the issuer advertised by the metadata to ValidIssuers, which would widen a pinned
            // ValidIssuer. A validator replaces that list outright and keeps the match exact.
            validation.IssuerValidator = (tokenIssuer, _, _) => string.Equals(tokenIssuer, issuer, StringComparison.Ordinal)
                ? tokenIssuer
                : throw new SecurityTokenInvalidIssuerException("The token issuer is not trusted.");

            validation.ValidateAudience = true;
            validation.ValidAudience = audience;
            validation.IgnoreTrailingSlashWhenValidatingAudience = false;

            validation.RequireSignedTokens = true;
            validation.ValidateIssuerSigningKey = true;
            validation.ValidateLifetime = true;
            validation.RequireExpirationTime = true;
            validation.ClockSkew = ClockSkew;
        });
    }
}

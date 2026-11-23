using System.Net;
using System.Text.Json;
using Ignixa.Federation.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Authentication;

public sealed class FederationCallerAuthenticationTests
{
    private const string BearerChallenge = "Bearer";

    [Fact]
    public async Task ValidToken_IsAccepted_AndKeepsRawRolesScpAndScopeClaims()
    {
        using SyntheticIdentityProvider provider = new();
        await using ProtectedEndpointHost host = await ProtectedEndpointHost.StartAsync(provider);

        // SMART scopes are refused later, by authorisation; authentication must pass them through untouched.
        ProtectedResponse response = await host.GetAsync(provider.IssueToken(new SyntheticTokenRequest
        {
            Roles = ["Federation.Patient.Read", "Federation.Observation.Read"],
            Scp = "Files.Read patient/*.read",
            Scope = "system/*.read launch",
        }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.ClaimValues("roles").ShouldBe(["Federation.Patient.Read", "Federation.Observation.Read"], ignoreOrder: true);
        response.ClaimValues("scp").ShouldBe(["Files.Read patient/*.read"]);
        response.ClaimValues("scope").ShouldBe(["system/*.read launch"]);
        response.Body!.Value.GetProperty("savedToken").ValueKind.ShouldBe(JsonValueKind.Null);
        provider.Requests.ShouldBe([SyntheticIdentityProvider.MetadataAddress, provider.JwksAddress]);
    }

    [Fact]
    public async Task MissingToken_IsChallenged()
    {
        using SyntheticIdentityProvider provider = new();
        await using ProtectedEndpointHost host = await ProtectedEndpointHost.StartAsync(provider);

        ProtectedResponse response = await host.GetAsync(token: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Challenges.ShouldBe([BearerChallenge]);
    }

    [Fact]
    public async Task MalformedToken_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, "not-a-jwt");
    }

    [Fact]
    public async Task WrongIssuer_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Issuer = "https://idp.example.test/tenant-b/v2.0" }));
    }

    [Fact]
    public async Task IssuerAdvertisedByMetadataButNotPinned_IsRejected()
    {
        const string advertised = "https://idp.example.test/advertised/v2.0";
        using SyntheticIdentityProvider provider = new() { AdvertisedIssuer = advertised };

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Issuer = advertised }));
    }

    [Fact]
    public async Task IssuerAdvertisedByMetadata_DoesNotStopThePinnedIssuerBeingAccepted()
    {
        using SyntheticIdentityProvider provider = new() { AdvertisedIssuer = "https://idp.example.test/advertised/v2.0" };
        await using ProtectedEndpointHost host = await ProtectedEndpointHost.StartAsync(provider);

        ProtectedResponse response = await host.GetAsync(provider.IssueToken());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WrongAudience_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Audience = "api://other" }));
    }

    [Fact]
    public async Task AudienceDifferingOnlyByTrailingSlash_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Audience = SyntheticIdentityProvider.TrustedAudience + "/" }));
    }

    [Fact]
    public async Task ConfiguredAudienceWithTrailingSlash_RejectsTheSlashlessAudience()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(
            provider,
            provider.IssueToken(),
            audience: SyntheticIdentityProvider.TrustedAudience + "/");
    }

    [Fact]
    public async Task AudienceDifferingOnlyByCase_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Audience = "API://FEDERATION" }));
    }

    [Fact]
    public async Task TokenSignedByAnUntrustedKey_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { UntrustedSigner = true }));
    }

    [Fact]
    public async Task UnsignedToken_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, SyntheticIdentityProvider.IssueUnsignedToken());
    }

    [Fact]
    public async Task TokenExpiredBeyondTheClockSkew_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Expires = DateTime.UtcNow.AddMinutes(-2) }));
    }

    [Fact]
    public async Task TokenExpiredWithinTheClockSkew_IsAccepted()
    {
        using SyntheticIdentityProvider provider = new();
        await using ProtectedEndpointHost host = await ProtectedEndpointHost.StartAsync(provider);

        ProtectedResponse response = await host.GetAsync(provider.IssueToken(new SyntheticTokenRequest { Expires = DateTime.UtcNow.AddSeconds(-30) }));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TokenNotYetValidBeyondTheClockSkew_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { NotBefore = DateTime.UtcNow.AddMinutes(2) }));
    }

    [Fact]
    public async Task TokenWithoutExpiry_IsRejected()
    {
        using SyntheticIdentityProvider provider = new();

        await AssertRejectedAsync(provider, provider.IssueToken(new SyntheticTokenRequest { Expires = null }));
    }

    [Fact]
    public async Task KeysPublishedOverHttp_AreNotTrusted()
    {
        using SyntheticIdentityProvider provider = new() { JwksAddress = new Uri("http://idp.example.test/tenant-a/discovery/keys") };

        await AssertRejectedAsync(provider, provider.IssueToken());

        provider.Requests.ShouldContain(SyntheticIdentityProvider.MetadataAddress);
        provider.Requests.ShouldNotContain(provider.JwksAddress);
    }

    [Theory]
    [InlineData("http://idp.example.test/tenant-a/v2.0")]
    [InlineData("/tenant-a/v2.0")]
    [InlineData("https://user@idp.example.test/tenant-a/v2.0")]
    [InlineData("https://idp.example.test/tenant-a/v2.0?query")]
    [InlineData("https://idp.example.test/tenant-a/v2.0#fragment")]
    public void Registration_RequiresAnUnambiguousHttpsAuthority(string authority)
    {
        AuthenticationBuilder builder = new ServiceCollection().AddAuthentication();

        Should.Throw<ArgumentException>(() => builder.AddFederationCallerJwtBearer(
            new Uri(authority, UriKind.RelativeOrAbsolute),
            SyntheticIdentityProvider.TrustedIssuer,
            SyntheticIdentityProvider.TrustedAudience));
    }

    [Theory]
    [InlineData("", "api://federation")]
    [InlineData("  ", "api://federation")]
    [InlineData("https://idp.example.test/tenant-a/v2.0", "")]
    [InlineData("https://idp.example.test/tenant-a/v2.0", " ")]
    public void Registration_RequiresAnExplicitIssuerAndAudience(string issuer, string audience)
    {
        AuthenticationBuilder builder = new ServiceCollection().AddAuthentication();

        Should.Throw<ArgumentException>(() => builder.AddFederationCallerJwtBearer(SyntheticIdentityProvider.Authority, issuer, audience));
    }

    private static async Task AssertRejectedAsync(
        SyntheticIdentityProvider provider,
        string token,
        string audience = SyntheticIdentityProvider.TrustedAudience)
    {
        await using ProtectedEndpointHost host = await ProtectedEndpointHost.StartAsync(provider, audience);

        ProtectedResponse response = await host.GetAsync(token);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Detailed challenge errors would tell an unauthenticated caller why validation failed.
        response.Challenges.ShouldBe([BearerChallenge]);
    }
}

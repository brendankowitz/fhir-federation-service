using System.Net.Http.Headers;
using System.Text.Json;
using Ignixa.Federation.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ignixa.Federation.Tests.Integration.Authentication;

/// <summary>
/// A test-only in-memory host with one protected endpoint, authenticated through the real named federation bearer
/// scheme. The identity provider is reached through the scheme's own <c>BackchannelHttpHandler</c> option, so the
/// production registration is exercised unchanged.
/// </summary>
internal sealed class ProtectedEndpointHost : IAsyncDisposable
{
    private const string ProtectedPath = "/test-only/protected";

    private readonly WebApplication _app;

    private ProtectedEndpointHost(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    private HttpClient Client { get; }

    public static async Task<ProtectedEndpointHost> StartAsync(
        SyntheticIdentityProvider provider,
        string audience = SyntheticIdentityProvider.TrustedAudience)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddAuthentication()
            .AddFederationCallerJwtBearer(SyntheticIdentityProvider.Authority, SyntheticIdentityProvider.TrustedIssuer, audience);
        builder.Services.AddAuthorization();
        builder.Services.Configure<JwtBearerOptions>(
            FederationCallerAuthentication.SchemeName,
            options => options.BackchannelHttpHandler = provider);

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet(ProtectedPath, DescribeCallerAsync)
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = FederationCallerAuthentication.SchemeName });

        await app.StartAsync();

        return new ProtectedEndpointHost(app, app.GetTestClient());
    }

    public async Task<ProtectedResponse> GetAsync(string? token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, ProtectedPath);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return new ProtectedResponse(
            response.StatusCode,
            [.. response.Headers.WwwAuthenticate.Select(h => h.ToString())],
            body.Length == 0 ? null : JsonSerializer.Deserialize<JsonElement>(body));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    private static async Task DescribeCallerAsync(HttpContext context)
    {
        await context.Response.WriteAsJsonAsync(new
        {
            claims = context.User.Claims.Select(c => new { type = c.Type, value = c.Value }),
            savedToken = await context.GetTokenAsync(FederationCallerAuthentication.SchemeName, "access_token"),
        });
    }
}

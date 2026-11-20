using Ignixa.Federation.Api.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ignixa.Federation.Api.Search;

/// <summary>Registers and maps the public primary search.</summary>
/// <remarks>
/// Without a <c>Federation:Host</c> section the routes still exist and answer 503, so an unconfigured host is visibly
/// not a search service while its health endpoints keep working. With the section, every dependency is resolved at
/// startup and any gap fails the host rather than the first request.
/// </remarks>
internal static class PrimarySearchEndpoints
{
    /// <summary>The Data Protection application discriminator; instances that share a key ring and this name redeem each other's tokens.</summary>
    private const string ApplicationName = "Ignixa.Federation";

    /// <summary>Registers the search services when the host is configured for search.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">The <c>Federation:Host</c> section is present but invalid.</exception>
    public static WebApplicationBuilder AddPrimarySearch(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RouteOptions>(o => o.SetParameterPolicy<ResourceTypeRouteConstraint>(ResourceTypeRouteConstraint.Name));
        if (SearchHostSettings.Read(builder.Configuration) is not { } settings)
        {
            return builder;
        }

        builder.Services.AddAuthentication().AddFederationCallerJwtBearer(settings.Authority, settings.Issuer, settings.Audience);
        builder.Services.AddDataProtection().SetApplicationName(ApplicationName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(services => PrimarySearch.Create(services, settings));
        return builder;
    }

    /// <summary>Maps <c>GET [type]</c> and <c>POST [type]/_search</c>; builds the search now when it is configured.</summary>
    /// <param name="app">The built application.</param>
    /// <returns>The same application.</returns>
    /// <exception cref="InvalidOperationException">Search is configured but cannot be built (see <see cref="PrimarySearch.Create"/>).</exception>
    public static WebApplication MapPrimarySearch(this WebApplication app)
    {
        PrimarySearch? search = app.Services.GetService<SearchHostSettings>() is null ? null : app.Services.GetRequiredService<PrimarySearch>();
        string route = "/{type:" + ResourceTypeRouteConstraint.Name + "}";
        app.MapGet(route, (HttpContext http, string type) => Serve(search, http, type, post: false));
        app.MapPost(route + "/_search", (HttpContext http, string type) => Serve(search, http, type, post: true));
        return app;
    }

    private static Task<IResult> Serve(PrimarySearch? search, HttpContext http, string type, bool post) =>
        search is null
            ? Task.FromResult(SearchOutcome.Unavailable("Search is not configured on this host."))
            : search.SearchAsync(http, type, post);
}

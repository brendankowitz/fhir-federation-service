using Ignixa.Federation.Api.Authentication;
using Ignixa.Federation.Api.Budgets;
using Ignixa.Federation.Api.Reads;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    private const string SigningUnavailable = "Caller signing configuration is not ready.";

    /// <summary>Registers the search services when the host is configured for search.</summary>
    /// <param name="builder">The application builder.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">The <c>Federation:Host</c> section is present but invalid.</exception>
    public static WebApplicationBuilder AddPrimarySearch(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<RouteOptions>(o =>
        {
            o.SetParameterPolicy<ResourceTypeRouteConstraint>(ResourceTypeRouteConstraint.Name);
            o.SetParameterPolicy<ResourceIdRouteConstraint>(ResourceIdRouteConstraint.Name);
        });
        if (SearchHostSettings.Read(builder.Configuration) is not { } settings)
        {
            return builder;
        }

        builder.Services.AddAuthentication().AddFederationCallerJwtBearer(settings.Authority, settings.Issuer, settings.Audience);
        builder.Services.AddDataProtection().SetApplicationName(ApplicationName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(services => new SigningConfiguration(
            settings.Authority,
            settings.Issuer,
            services.GetRequiredService<TimeProvider>(),
            () => services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(FederationCallerAuthentication.SchemeName).BackchannelHttpHandler,
            services.GetRequiredService<ILogger<SigningConfiguration>>()));
        builder.Services.AddHostedService(services => services.GetRequiredService<SigningConfiguration>());
        builder.Services.AddHealthChecks().AddCheck<SigningConfiguration>(SigningConfiguration.HealthCheckName, tags: ["ready"]);
        builder.Services.AddOptions<JwtBearerOptions>(FederationCallerAuthentication.SchemeName)
            .Configure<SigningConfiguration>((options, signing) => options.ConfigurationManager = signing);
        // The handler logs token validation exceptions from the library. Keep detailed token data out of host logs.
        builder.Logging.AddFilter(typeof(JwtBearerHandler).FullName, LogLevel.None);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(services => PrimarySearch.Create(services, settings));
        return builder;
    }

    /// <summary>Starts the single search budget after routing but before the explicit authentication middleware.</summary>
    public static void UsePrimarySearchBudget(this WebApplication app)
    {
        var search = app.Services.GetRequiredService<PrimarySearch>();
        var signing = app.Services.GetRequiredService<SigningConfiguration>();
        app.Use((http, next) => http.GetEndpoint()?.Metadata.GetMetadata<SearchEndpoint>() is null
            ? next(http)
            : ServeWithBudgetAsync(http, next, search, signing));
    }

    /// <summary>Maps <c>GET [type]</c> and <c>POST [type]/_search</c>; builds the search now when it is configured.</summary>
    /// <param name="app">The built application.</param>
    /// <returns>The same application.</returns>
    /// <exception cref="InvalidOperationException">Search is configured but cannot be built (see <see cref="PrimarySearch.Create"/>).</exception>
    public static WebApplication MapPrimarySearch(this WebApplication app)
    {
        PrimarySearch? search = app.Services.GetService<SearchHostSettings>() is null ? null : app.Services.GetRequiredService<PrimarySearch>();
        string route = "/{type:" + ResourceTypeRouteConstraint.Name + "}";
        app.MapGet(route, (HttpContext http, string type) => Serve(search, http, type, post: false)).WithMetadata(SearchEndpoint.Marker);
        app.MapPost(route + "/_search", (HttpContext http, string type) => Serve(search, http, type, post: true)).WithMetadata(SearchEndpoint.Marker);

        // The proxied read is an owner-qualified read. It shares the search budget and signing check through the marker.
        // The unqualified form is ambiguous across owners: it carries no marker, needs no authentication, and never looks anything up.
        ProxiedReads? reads = search?.Reads;
        app.MapGet(
            "/shards/{alias}/{type:" + ResourceTypeRouteConstraint.Name + "}/{id}",
            (HttpContext http, string alias, string type, string id) => ServeRead(search, http, alias, type, id)).WithMetadata(SearchEndpoint.Marker);
        app.MapGet(
            "/{type:" + ResourceTypeRouteConstraint.Name + "}/{id:" + ResourceIdRouteConstraint.Name + "}",
            () => ProxiedReads.Ambiguous(reads?.Enabled == true));
        return app;
    }

    private static async Task ServeWithBudgetAsync(HttpContext http, RequestDelegate next, PrimarySearch search, SigningConfiguration signing)
    {
        using RequestBudget budget = search.StartBudget(http.RequestAborted);
        if (signing.FreshPublicationTimestamp is not { } publishedAt)
        {
            IResult result = SearchOutcome.Unavailable(SigningUnavailable);
            try
            {
                budget.Checkpoint();
            }
            catch (RequestDeadlineExceededException)
            {
                result = SearchOutcome.TimedOut();
            }

            await result.ExecuteAsync(http);
            return;
        }

        http.Features.Set(new SearchRequest(budget, signing, publishedAt));
        await next(http);
    }

    private static Task<IResult> Serve(PrimarySearch? search, HttpContext http, string type, bool post) =>
        search is null
            ? Task.FromResult(SearchOutcome.Unavailable("Search is not configured on this host."))
            : search.SearchAsync(http, type, post, http.Features.GetRequiredFeature<SearchRequest>());

    private static Task<IResult> ServeRead(PrimarySearch? search, HttpContext http, string alias, string type, string id) =>
        search is null
            ? Task.FromResult(SearchOutcome.Unavailable("Search is not configured on this host."))
            : search.Reads.ReadAsync(http, alias, type, id, http.Features.GetRequiredFeature<SearchRequest>());

    private sealed class SearchEndpoint
    {
        public static SearchEndpoint Marker { get; } = new();
    }

    /// <summary>Per-search feature holding the budget and the pre-authentication signing freshness bound.</summary>
    internal sealed record SearchRequest(RequestBudget Budget, SigningConfiguration Signing, long SigningPublishedAt)
    {
        // A refresh completing concurrently must not extend an in-flight authentication's freshness bound.
        public IResult? CheckSigning() => Signing.IsFresh(SigningPublishedAt) ? null : SearchOutcome.Unavailable(SigningUnavailable);
    }
}

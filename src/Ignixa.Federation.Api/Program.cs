using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Metadata;
using Ignixa.Federation.Api.Search;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.AddPrimarySearch();

WebApplication app = builder.Build();

var live = new HealthCheckOptions { Predicate = _ => false };
var ready = new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") };
if (app.Services.GetService<SearchHostSettings>() is not null)
{
    // Health must not enter bearer authentication, even when the probe sends a bearer token.
    app.UseHealthChecks("/health/live", live);
    app.UseHealthChecks("/health/ready", ready);

    app.UseRouting();
    app.UsePrimarySearchBudget();
    app.UseAuthentication();
}
else
{
    app.MapHealthChecks("/health/live", live);
    app.MapHealthChecks("/health/ready", ready);
}

app.MapPrimarySearch();
app.MapFederatedMetadata();

app.Run();

/// <summary>Entry point marker so integration tests can host the application with <c>WebApplicationFactory</c>.</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "WebApplicationFactory<Program> needs a public entry-point type.")]
public partial class Program;

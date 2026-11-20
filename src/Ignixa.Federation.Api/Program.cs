using System.Diagnostics.CodeAnalysis;
using Ignixa.Federation.Api.Search;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.AddPrimarySearch();

WebApplication app = builder.Build();

// Liveness runs no checks: the process answering is the signal.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness runs only checks tagged "ready" (shard checks register with this tag).
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapPrimarySearch();

app.Run();

/// <summary>Entry point marker so integration tests can host the application with <c>WebApplicationFactory</c>.</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "WebApplicationFactory<Program> needs a public entry-point type.")]
public partial class Program;

using System.Diagnostics.CodeAnalysis;
using Ignixa.Abstractions;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Search.Definition;
using Ignixa.Search.Expressions.Parsers;
using Ignixa.Search.Indexing;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Parsing;
using Ignixa.Specification.Extensions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddOptions<FederationOptions>()
    .Bind(builder.Configuration.GetSection(FederationOptions.SectionName))
    .Validate(options => FederationOptionsValidator.IsCertifiedVersion(options.Version), "Federation:Version: must be a certified release.")
    .ValidateOnStart();
builder.Services.AddSingleton<IFhirSchemaProvider>(services =>
    Enum.Parse<FhirVersion>(services.GetRequiredService<IOptions<FederationOptions>>().Value.Version).GetSchemaProvider());
builder.Services.AddSingleton<SearchParameterDefinitionManager>();
builder.Services.AddSingleton<IFederationSearchSpecification>(services =>
{
    IFhirSchemaProvider schema = services.GetRequiredService<IFhirSchemaProvider>();
    SearchParameterDefinitionManager definitions = services.GetRequiredService<SearchParameterDefinitionManager>();
    SearchOptionsBuilder CreateCompiler()
    {
        var references = new ReferenceSearchValueParser(schema, NullFhirBaseUriProvider.Instance);
        var parser = new ExpressionParser(() => definitions, new SearchParameterExpressionParser(references, schema), schema);
        return new SearchOptionsBuilder(parser, definitions);
    }

    string[] acceptedVersions = schema.Version == FhirVersion.R4
        ? ["4.0.0", "4.0.1"]
        : [schema.FullVersion];
    return new FederationSearchSpecification(schema.Version, schema, definitions, CreateCompiler, acceptedVersions);
});

WebApplication app = builder.Build();

// Liveness runs no checks: the process answering is the signal.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness runs only checks tagged "ready" (shard checks register with this tag).
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

/// <summary>Entry point marker so integration tests can host the application with <c>WebApplicationFactory</c>.</summary>
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "WebApplicationFactory<Program> needs a public entry-point type.")]
public partial class Program;

using System.Diagnostics.CodeAnalysis;
using Ignixa.Abstractions;
using Ignixa.Federation.Api.Metadata;
using Ignixa.Federation.Api.Search;
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
// Other federation settings are bound when a capability view is prepared, including on cold hosts.
builder.Services.AddOptions<FederationOptions>()
    .Configure(options => options.Version = builder.Configuration[$"{FederationOptions.SectionName}:Version"] ?? options.Version)
    .Validate(options =>
    {
        IConfigurationSection? version = builder.Configuration.GetSection(FederationOptions.SectionName).GetChildren()
            .FirstOrDefault(section => string.Equals(section.Key, nameof(FederationOptions.Version), StringComparison.OrdinalIgnoreCase));
        return (version is null || (version.Value is not null && !version.GetChildren().Any()))
            && FederationOptionsValidator.IsCertifiedVersion(options.Version);
    }, "Federation:Version: must be a certified release.")
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

    return new FederationSearchSpecification(schema.Version, schema, definitions, CreateCompiler);
});
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

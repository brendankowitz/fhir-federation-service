using Ignixa.Abstractions;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Identity;
using Ignixa.Search.Definition;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration;

public sealed class VersionStartupTests
{
    [Theory]
    [InlineData("R5", FhirVersion.R5)]
    [InlineData("R6", FhirVersion.R6)]
    public void WhenAnotherReleaseIsCertified_HostUsesItsSchemaVersion(string version, FhirVersion expected)
    {
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:Version"] = version }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IValidateOptions<FederationOptions>>();
                services.AddOptions<FederationOptions>().Validate(options => options.Version == version);
            });
        });
        using var client = factory.CreateClient();

        var specification = factory.Services.GetRequiredService<IFederationSearchSpecification>();
        specification.Version.ShouldBe(expected);
        specification.PublishedVersion.ShouldBe(specification.Schema.FullVersion);
        specification.AcceptedShardVersions.ShouldBe([specification.Schema.FullVersion]);
        specification.AcceptedShardVersions.Contains("4.0.1").ShouldBeFalse();
    }

    [Fact]
    public void DefaultHost_ComposesOneR4SpecificationAndSchema()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        IServiceProvider services = factory.Services;
        var specification = services.GetRequiredService<IFederationSearchSpecification>();
        var schema = services.GetRequiredService<IFhirSchemaProvider>();

        specification.ShouldBeSameAs(services.GetRequiredService<IFederationSearchSpecification>());
        schema.ShouldBeSameAs(services.GetRequiredService<IFhirSchemaProvider>());
        specification.Schema.ShouldBeSameAs(schema);
        specification.Version.ShouldBe(FhirVersion.R4);
        specification.PublishedVersion.ShouldBe(schema.FullVersion);
        specification.AcceptedShardVersions.ShouldBe(["4.0.0", "4.0.1"], ignoreOrder: true);
        specification.Compile(ResourceType.Parse("Patient"), "name", "Smith", SearchFailureCode.UnsupportedParameter, out _)
            .ShouldNotBeNull().Value.ShouldBe("Smith");
        services.GetRequiredService<SearchParameterDefinitionManager>()
            .ShouldBeSameAs(services.GetRequiredService<SearchParameterDefinitionManager>());
    }

    [Theory]
    [InlineData("R5")]
    [InlineData("R6")]
    [InlineData("invalid")]
    [InlineData("r4")]
    [InlineData("4")]
    public void UncertifiedOrInvalidVersion_FailsStartup(string version)
    {
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:Version"] = version })));

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Failures.ShouldContain(f => f.StartsWith("Federation:Version:", StringComparison.Ordinal));
    }
}

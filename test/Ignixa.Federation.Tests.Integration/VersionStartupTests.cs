using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
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
        AssertVersionFailsStartup(new Dictionary<string, string?> { ["Federation:Version"] = version });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NullOrStructuredVersion_FailsStartup(bool hasScalar, bool hasChild)
    {
        var settings = new Dictionary<string, string?> { ["Federation:Version"] = hasScalar ? "R4" : null };
        if (hasChild)
        {
            settings["Federation:Version:0"] = "R4";
        }

        AssertVersionFailsStartup(settings);
    }

    private static void AssertVersionFailsStartup(Dictionary<string, string?> settings)
    {
        using var failures = new StartupFailures();
        using var root = new WebApplicationFactory<Program>();
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging.AddProvider(failures));
        });

        Exception? thrown = Record.Exception(() => factory.CreateClient());
        thrown.ShouldNotBeNull();
        OptionsValidationException validation;
        if (thrown is OptionsValidationException direct)
        {
            validation = direct;
        }
        else
        {
            // DeferredHostBuilder can surface disposal after the underlying host logged its startup failure.
            thrown.ShouldBeOfType<ObjectDisposedException>();
            validation = failures.Exceptions.OfType<OptionsValidationException>().ShouldHaveSingleItem();
        }

        validation.Failures.ShouldContain(f => f.StartsWith("Federation:Version:", StringComparison.Ordinal));
    }

    private sealed class StartupFailures : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Exceptions.Enqueue(exception);
            }
        }

        public void Dispose()
        {
        }
    }
}

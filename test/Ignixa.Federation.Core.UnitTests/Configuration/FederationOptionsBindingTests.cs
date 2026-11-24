using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Microsoft.Extensions.Configuration;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Configuration;

public sealed class FederationOptionsBindingTests
{
    [Theory]
    [InlineData("R5")]
    [InlineData("R6")]
    [InlineData("not-a-release")]
    [InlineData("r4")]
    [InlineData("4.0.1")]
    [InlineData("")]
    public void ConfiguredUncertifiedOrInvalidVersion_IsRejected(string version)
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(SpecExample));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(json)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Federation:Version"] = version })
            .Build();
        var options = new FederationOptions();
        configuration.GetSection(FederationOptions.SectionName).Bind(options);

        var result = new FederationOptionsValidator([MicrosoftFhirServer]).Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.StartsWith("Federation:Version:", StringComparison.Ordinal));
    }

    // The design spec §4.2 example, verbatim, wrapped in braces to make it a document.
    private const string SpecExample = """
        {
        "Federation": {
          "ProxyReads": true,
          "ChainResolution": {
            "Default": "Distributed",
            "Passthrough": [ "Observation.subject:Patient", "Encounter.subject:Patient", "Condition.subject:Patient" ]
          },
          "Shards": [
            { "Id": "east", "Alias": "k7q2", "BaseUrl": "https://fhir-east.internal/", "Profile": "MicrosoftFhirServer",
              "Auth": { "Type": "ManagedIdentity", "Resource": "https://fhir-east.internal" },
              "LinkBaseAliases": [] },
            { "Id": "west", "Alias": "p9xm", "BaseUrl": "https://fhir-west.internal/", "Profile": "MicrosoftFhirServer",
              "Auth": { "Type": "ManagedIdentity", "Resource": "https://fhir-west.internal" } }
          ]
        }
        }
        """;

    [Fact]
    public void SpecExample_BindsIntoTheGetOnlyCollections_AndBuildsAValidRegistry()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(SpecExample));
        IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        var options = new FederationOptions();

        configuration.GetSection(FederationOptions.SectionName).Bind(options);

        options.ChainResolution.Passthrough.ShouldBe(["Observation.subject:Patient", "Encounter.subject:Patient", "Condition.subject:Patient"]);
        options.Version.ShouldBe("R4");
        options.Limits.DefaultPageSize.ShouldBe(20);
        new FederationOptionsValidator([MicrosoftFhirServer]).Validate(null, options).Succeeded.ShouldBeTrue();

        ShardRegistry registry = ShardRegistry.Create(options.Shards, [MicrosoftFhirServer]);

        registry.Shards.Select(s => (s.Id.Value, s.Alias.Value, s.BaseUrl.ToString(), s.Ordinal))
            .ShouldBe([("east", "k7q2", "https://fhir-east.internal/", 0), ("west", "p9xm", "https://fhir-west.internal/", 1)]);
        registry.Shards.ShouldAllBe(s => s.Auth is ManagedIdentityAuth);
    }
}

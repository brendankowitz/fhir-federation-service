using System.Net;
using System.Text.Json;
using Ignixa.Abstractions;
using Ignixa.Federation.Api.Search;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Tests.Integration.Search;
using Ignixa.Specification.ValueSets.Normative;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Metadata;

public sealed class MetadataReleaseTests
{
    [Fact]
    public async Task MetadataPublishesTheAdmittedR5ViewVersion()
    {
        IFederationSearchSpecification specification = TestSpecifications.Create(FhirVersion.R5);
        using var host = SearchHost.Start(setup =>
        {
            setup.Capabilities = View(specification, "5.0.0");
            setup.ConfigureServices = services => services.AddSingleton(specification);
        });

        using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/metadata", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("fhirVersion").GetString().ShouldBe("5.0.0");
    }

    [Theory]
    [InlineData(FhirVersion.R4, "4.0.1")]
    [InlineData(FhirVersion.R5, "5.0.0")]
    public async Task PublishingAnotherSpecificationIsRejectedWithoutReplacingTheCurrentView(FhirVersion release, string shardVersion)
    {
        using var host = SearchHost.Start();
        var publisher = host.Services.GetRequiredService<IFederationCapabilityPublisher>();
        SearchCapabilities foreign = View(TestSpecifications.Create(release), shardVersion);

        Should.Throw<InvalidOperationException>(() => publisher.Publish(foreign));
        using HttpResponseMessage response = await host.Client.GetAsync(new Uri("/metadata", UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("fhirVersion").GetString().ShouldBe("4.0.1");
    }

    private static SearchCapabilities View(IFederationSearchSpecification specification, string shardVersion)
    {
        var resource = new ResourceSearchSupport(ResourceType.Parse("Patient"), [], [], []);
        var certification = new SearchCertification(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);
        return new SearchCapabilities(SearchWorld.Registry, SearchWorld.Registry.Shards.Select(shard =>
            new ShardSearchCapability(shard.Id, [resource], certification, shardVersion)), specification);
    }
}

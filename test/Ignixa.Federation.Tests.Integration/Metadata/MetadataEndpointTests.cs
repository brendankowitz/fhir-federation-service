using System.Globalization;
using System.Net;
using System.Text.Json;
using Ignixa.Federation.Core.Identity;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Tests.Integration.Search;
using Ignixa.Specification.ValueSets.Normative;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Metadata;

public sealed class MetadataEndpointTests
{
    [Fact]
    public async Task ConfiguredHostPublishesOnlyCommonSearchCapabilitiesWithoutCallingShards()
    {
        SearchCapabilities capabilities = UnlikeShards();
        // Even a planner-admitted include must stay out until the host actually executes it.
        capabilities.Resources.Single(r => r.Type.Value == "Patient").SearchIncludes.ShouldBe(["Patient:organization"]);
        using var host = SearchHost.Start(setup => setup.Capabilities = capabilities);

        using HttpResponseMessage response = await GetAsync(host, "/metadata");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/fhir+json");
        using JsonDocument document = await ReadAsync(response);
        JsonElement statement = document.RootElement;
        statement.GetProperty("resourceType").GetString().ShouldBe("CapabilityStatement");
        statement.GetProperty("status").GetString().ShouldBe("active");
        DateTimeOffset.TryParse(statement.GetProperty("date").GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _).ShouldBeTrue();
        statement.GetProperty("kind").GetString().ShouldBe("instance");
        statement.GetProperty("implementation").GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
        statement.GetProperty("implementation").GetProperty("url").GetString().ShouldBe(SearchWorld.PublicBase);
        statement.GetProperty("fhirVersion").GetString().ShouldBe("4.0.1");
        statement.GetProperty("format").EnumerateArray().Select(f => f.GetString()).ShouldBe(["json"]);

        JsonElement rest = statement.GetProperty("rest").EnumerateArray().ShouldHaveSingleItem();
        rest.GetProperty("mode").GetString().ShouldBe("server");
        rest.TryGetProperty("interaction", out _).ShouldBeFalse();
        JsonElement[] resources = [.. rest.GetProperty("resource").EnumerateArray()];
        resources.Select(r => r.GetProperty("type").GetString()).ShouldBe(["Organization", "Patient"], ignoreOrder: true);
        foreach (JsonElement resource in resources)
        {
            resource.GetProperty("interaction").EnumerateArray()
                .Select(i => i.GetProperty("code").GetString()).ShouldBe(["search-type"]);
            resource.TryGetProperty("searchInclude", out _).ShouldBeFalse();
            resource.TryGetProperty("searchRevInclude", out _).ShouldBeFalse();
        }

        JsonElement patientParameter = resources.Single(r => r.GetProperty("type").GetString() == "Patient")
            .GetProperty("searchParam").EnumerateArray().ShouldHaveSingleItem();
        patientParameter.GetProperty("name").GetString().ShouldBe("name");
        patientParameter.GetProperty("type").GetString().ShouldBe("string");
        patientParameter.GetProperty("definition").GetString().ShouldBe("http://hl7.org/fhir/SearchParameter/Patient-name");
        JsonElement organizationParameter = resources.Single(r => r.GetProperty("type").GetString() == "Organization")
            .GetProperty("searchParam").EnumerateArray().ShouldHaveSingleItem();
        organizationParameter.GetProperty("name").GetString().ShouldBe("_id");
        organizationParameter.GetProperty("type").GetString().ShouldBe("token");
        host.Shards.Views.ShouldBeEmpty();

        (await host.GetAsync("/Patient?_include=Patient:organization")).IssueCode.ShouldBe("not-supported");
        (await host.GetAsync("/Patient?_sort=_lastUpdated")).IssueCode.ShouldBe("not-supported");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConfiguredHostWithNoCommonResourcesPublishesAnEmptyServerCapability()
    {
        var certification = new SearchCertification(new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [], [], []);
        var capabilities = new SearchCapabilities(SearchWorld.Registry,
        [
            new(ShardId.Parse("east"), [new(ResourceType.Parse("Observation"), [], [], [])], certification),
            new(ShardId.Parse("west"), [new(ResourceType.Parse("Encounter"), [], [], [])], certification),
        ]);
        using var host = SearchHost.Start(setup => setup.Capabilities = capabilities);
        using HttpResponseMessage response = await GetAsync(host, "/metadata?_format=application%2Ffhir%2Bjson");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument document = await ReadAsync(response);
        JsonElement rest = document.RootElement.GetProperty("rest").EnumerateArray().ShouldHaveSingleItem();
        rest.GetProperty("mode").GetString().ShouldBe("server");
        rest.TryGetProperty("resource", out _).ShouldBeFalse();
        host.Shards.Views.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnconfiguredHostDoesNotPublishCapabilitiesAndKeepsHealthEndpoints()
    {
        using var host = SearchHost.Start(setup =>
        {
            setup.Unconfigure();
            setup.Capabilities = null;
            setup.RegisterTransport = false;
        });

        using HttpResponseMessage response = await GetAsync(host, "/metadata");
        using HttpResponseMessage live = await GetAsync(host, "/health/live");
        using HttpResponseMessage ready = await GetAsync(host, "/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task XmlFormatIsNotAvailable()
    {
        using var host = SearchHost.Start();
        using HttpResponseMessage response = await GetAsync(host, "/metadata?_format=xml");
        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        host.Shards.Views.ShouldBeEmpty();
    }

    private static SearchCapabilities UnlikeShards()
    {
        var patient = ResourceType.Parse("Patient");
        var organization = ResourceType.Parse("Organization");
        var name = new SearchParameterSupport("name", SearchParamType.String, definition: null);
        var family = new SearchParameterSupport("family", SearchParamType.String, definition: null);
        var subject = new SearchParameterSupport("organization", SearchParamType.Reference, definition: null);
        var commonOrganization = new ResourceSearchSupport(
            organization, [new("_id", SearchParamType.Token, definition: null)], [], []);
        var include = new ReferencePathCertification(new ReferencePath(patient, "organization", organization),
            ReferencePathOperations.IncludeExtraction, ReferenceSearchForms.None, ReferenceSearchForms.None);
        var certification = new SearchCertification(
            new Dictionary<SearchParamType, IReadOnlyCollection<string>>(), [], [SearchParamType.Token], [], [include]);
        var eastPatient = new ResourceSearchSupport(patient, [name, family, subject], ["*", "Patient:organization"], []);
        var westPatient = new ResourceSearchSupport(patient, [name, subject], ["*", "Patient:organization"], []);
        return new SearchCapabilities(SearchWorld.Registry,
        [
            new(ShardId.Parse("east"), [eastPatient, commonOrganization, new(ResourceType.Parse("Observation"), [], [], [])], certification),
            new(ShardId.Parse("west"), [westPatient, commonOrganization, new(ResourceType.Parse("Encounter"), [], [], [])], certification),
        ]);
    }

    private static Task<HttpResponseMessage> GetAsync(SearchHost host, string path) =>
        host.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response)
    {
        using Stream body = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: TestContext.Current.CancellationToken);
    }
}

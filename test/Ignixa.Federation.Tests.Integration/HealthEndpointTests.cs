using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoint_ReturnsOk(string path)
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FailingReadyCheck_FailsReadinessButNotLiveness()
    {
        using WebApplicationFactory<Program> unhealthy = WithFailingCheck(tag: "ready");

        (await GetStatusAsync(unhealthy, "/health/live")).ShouldBe(HttpStatusCode.OK);
        (await GetStatusAsync(unhealthy, "/health/ready")).ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task FailingCheckWithoutReadyTag_IsIgnoredByBothEndpoints()
    {
        using WebApplicationFactory<Program> unhealthy = WithFailingCheck(tag: "other");

        (await GetStatusAsync(unhealthy, "/health/live")).ShouldBe(HttpStatusCode.OK);
        (await GetStatusAsync(unhealthy, "/health/ready")).ShouldBe(HttpStatusCode.OK);
    }

    private WebApplicationFactory<Program> WithFailingCheck(string tag) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddHealthChecks().AddCheck("always-failing", () => HealthCheckResult.Unhealthy("test"), tags: [tag])));

    private static async Task<HttpStatusCode> GetStatusAsync(WebApplicationFactory<Program> host, string path)
    {
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        return response.StatusCode;
    }
}

using System.Net;
using Ignixa.Federation.Tests.Integration.Authentication;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Search;

/// <summary>Who may search: the federation bearer scheme and the role grants, checked before any shard call.</summary>
public sealed class PrimarySearchAuthorizationTests
{
    [Fact]
    public async Task MissingToken_IsChallenged_WithoutAnyShardCall()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Patient?name=Orchid", token: null);

        result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        result.Challenges.ShouldContain(c => c.StartsWith("Bearer", StringComparison.Ordinal));
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UntrustedToken_IsChallenged_WithoutAnyShardCall()
    {
        using var host = SearchHost.Start();
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], UntrustedSigner = true });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid", token);

        result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task PostWithoutToken_IsChallengedBeforeTheBodyIsRead()
    {
        using var host = SearchHost.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Patient/_search")
        {
            Content = new StringContent(new string('a', 100_000), System.Text.Encoding.UTF8, "text/plain"),
        };

        SearchResult result = await host.SendAsync(request, token: null);

        result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RoleWithoutTheType_IsForbidden_WithoutAnyShardCall()
    {
        using var host = SearchHost.Start();
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole] });

        SearchResult result = await host.GetAsync("/Patient?name=Orchid", token);

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        result.IssueCode.ShouldBe("forbidden");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoRoles_IsForbidden()
    {
        using var host = SearchHost.Start();
        string token = host.Token(new SyntheticTokenRequest());

        SearchResult result = await host.GetAsync("/Patient", token);

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("patient/*.read", null)]
    [InlineData(null, "user/Patient.rs")]
    public async Task SmartScopes_AreForbiddenEvenWithAGrantedRole(string? scp, string? scope)
    {
        using var host = SearchHost.Start();
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], Scp = scp, Scope = scope });

        SearchResult result = await host.GetAsync("/Patient", token);

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task UngrantedButWellFormedType_IsForbidden()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/Practitioner?name=x");

        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/patient")]
    [InlineData("/PATIENT1")]
    [InlineData("/Patient/_history")]
    public async Task NonResourceTypePaths_AreNotSearchRoutes(string path)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync(path);

        result.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        host.Shards.Calls.ShouldBeEmpty();
    }
}

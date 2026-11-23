using System.Net;
using Ignixa.Federation.Tests.Integration.Search;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>The two read routes exist on the real host (design spec §5, §5.1), independent of any shard binding.</summary>
public sealed class ProxiedReadRouteTests
{
    [Theory]
    [InlineData("/Patient/dup")]
    [InlineData("/Observation/obs-dup")]
    public async Task UnqualifiedTypedRead_IsAmbiguous_WithoutAuthenticationOrAnyShardCall(string path)
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync(path, token: null);

        result.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.IssueCode.ShouldBe("invalid");
        result.Body.ShouldContain("fullUrl");
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProxiedRoute_ExistsAndChallengesAnonymousCallers()
    {
        using var host = SearchHost.Start();

        SearchResult result = await host.GetAsync("/shards/k7q2/Patient/dup", token: null);

        result.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        result.Challenges.ShouldContain(c => c.StartsWith("Bearer", StringComparison.Ordinal));
        host.Shards.Calls.ShouldBeEmpty();
    }
}

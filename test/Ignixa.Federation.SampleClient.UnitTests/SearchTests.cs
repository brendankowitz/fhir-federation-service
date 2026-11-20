using System.Net;
using Shouldly;
using static Ignixa.Federation.SampleClient.UnitTests.TestBundles;
using static Ignixa.Federation.SampleClient.UnitTests.TestClients;

namespace Ignixa.Federation.SampleClient.UnitTests;

/// <summary>Identity, paging and response classification, driven through the public client API.</summary>
public class SearchTests
{
    [Fact]
    public async Task Colliding_ids_from_different_shards_stay_distinct_entries()
    {
        var handler = new ScriptedHandler().Ok(Page(null,
            Match($"{East}Patient/123", "Patient", "123", shard: "k7q2"),
            Match($"{West}Patient/123", "Patient", "123", shard: "p9xm")));
        using var client = Create(handler);

        var results = await SearchAll(client);

        results.Matches.Count.ShouldBe(2);
        results.Matches.Select(m => m.Id).ShouldBe(["123", "123"]);
        results.Matches[0].FullUrl.ShouldBe(new Uri($"{East}Patient/123"));
        results.Matches[1].FullUrl.ShouldBe(new Uri($"{West}Patient/123"));
        results.Matches[0].ShouldNotBe(results.Matches[1]);
    }

    [Fact]
    public async Task Includes_repeated_on_a_later_page_are_deduplicated_by_full_url()
    {
        var handler = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct=a",
                Match($"{East}Patient/1", "Patient", "1"),
                Include($"{East}Organization/9", "Organization", "9")))
            .Ok(Page(null,
                Match($"{East}Patient/2", "Patient", "2"),
                Include($"{East}Organization/9", "Organization", "9"),
                Include($"{West}Organization/9", "Organization", "9")));
        using var client = Create(handler);

        var results = await SearchAll(client);

        results.Matches.Count.ShouldBe(2);
        results.Includes.Select(i => i.FullUrl.AbsoluteUri)
            .ShouldBe([$"{East}Organization/9", $"{West}Organization/9"]);
    }

    [Fact]
    public async Task Initial_request_targets_the_type_endpoint_with_escaped_parameters()
    {
        var handler = new ScriptedHandler().Ok(Page());
        using var client = Create(handler);

        await SearchResultSet.FromAsync(client.SearchAsync(
            "Patient", [new("name", "van der Berg"), new("_count", "2")], TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Uri.AbsoluteUri.ShouldBe($"{Fed}Patient?name=van%20der%20Berg&_count=2");
        request.Accept.ShouldBe("application/fhir+json");
    }

    [Fact]
    public async Task Next_link_is_followed_verbatim_and_paging_stops_without_next()
    {
        const string ct = "q%2Bw%3D%3D-x%2Fy";
        var handler = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct={ct}", Match($"{East}Patient/1", "Patient", "1")))
            .Ok(Page(null, Match($"{East}Patient/2", "Patient", "2")));
        using var client = Create(handler);

        var pages = new List<SearchPage>();
        await foreach (var page in client.SearchAsync("Patient", [], TestContext.Current.CancellationToken))
        {
            pages.Add(page);
        }

        pages.Select(p => p.HasNext).ShouldBe([true, false]);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[1].Uri.Query.ShouldBe($"?ct={ct}");
    }

    [Fact]
    public async Task Relative_next_link_resolves_against_the_federation_base()
    {
        var handler = new ScriptedHandler().Ok(Page("Patient?ct=abc")).Ok(Page());
        using var client = Create(handler);

        await SearchAll(client);

        handler.Requests[1].Uri.AbsoluteUri.ShouldBe($"{Fed}Patient?ct=abc");
    }

    [Fact]
    public async Task A_next_link_that_was_already_followed_is_an_error()
    {
        var handler = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct=A"))
            .Ok(Page($"{Fed}Patient?ct=A"));
        using var client = Create(handler);

        await Should.ThrowAsync<InvalidDataException>(() => SearchAll(client));

        handler.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("Pat/ient")]
    [InlineData("Patient?x=1")]
    [InlineData("")]
    public void Resource_type_must_be_a_simple_token(string type)
    {
        var handler = new ScriptedHandler();
        using var client = Create(handler);

        Should.Throw<ArgumentException>(() => client.SearchAsync(type, []));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Callers_cannot_supply_the_continuation_parameter()
    {
        var handler = new ScriptedHandler();
        using var client = Create(handler);

        Should.Throw<ArgumentException>(() => client.SearchAsync("Patient", [new("ct", "forged")]));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Entries_are_classified_and_carry_source_shard_alternate_and_resource()
    {
        var alternate = $"{Fed}shards/k7q2/Patient/1";
        var handler = new ScriptedHandler().Ok(Page(null,
            Match($"{East}Patient/1", "Patient", "1", shard: "k7q2", alternate: alternate),
            Include($"{East}Organization/9", "Organization", "9", shard: "k7q2"),
            OutcomeEntry("warning", "incomplete", "Included resources were truncated.")));
        using var client = Create(handler);

        SearchPage? page = null;
        await foreach (var p in client.SearchAsync("Patient", [], TestContext.Current.CancellationToken))
        {
            page = p;
        }

        page.ShouldNotBeNull();
        var match = page.Matches.ShouldHaveSingleItem();
        match.Mode.ShouldBe(EntryMode.Match);
        match.ResourceType.ShouldBe("Patient");
        match.SourceShard.ShouldBe("k7q2");
        match.Alternate.ShouldBe(new Uri(alternate));
        match.Resource.GetProperty("id").GetString().ShouldBe("1");

        var include = page.Includes.ShouldHaveSingleItem();
        include.Mode.ShouldBe(EntryMode.Include);
        include.ResourceType.ShouldBe("Organization");
        include.Alternate.ShouldBeNull();

        var outcome = page.Outcomes.ShouldHaveSingleItem();
        outcome.Severity.ShouldBe("warning");
        outcome.Code.ShouldBe("incomplete");
        outcome.Diagnostics.ShouldBe("Included resources were truncated.");
    }

    [Fact]
    public async Task Error_status_surfaces_operation_outcome_issues_without_leaking_the_body()
    {
        var longDiagnostics = new string('x', 500);
        var handler = new ScriptedHandler().Respond(
            HttpStatusCode.BadGateway, OperationOutcome("error", "exception", longDiagnostics));
        using var client = Create(handler);

        var ex = await Should.ThrowAsync<FederationRequestException>(() => SearchAll(client));

        ex.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var issue = ex.Issues.ShouldHaveSingleItem();
        (issue.Severity, issue.Code).ShouldBe(("error", "exception"));
        issue.Diagnostics!.Length.ShouldBe(200);
        ex.Message.ShouldContain("502");
        ex.Message.ShouldNotContain("xxx");
        ex.Message.ShouldNotContain(FederationToken);
    }

    [Fact]
    public async Task Error_status_with_a_non_fhir_body_has_no_issues_and_does_not_echo_the_body()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.InternalServerError, "<html>secret-body ct=SECRETCT</html>");
        using var client = Create(handler);

        var ex = await Should.ThrowAsync<FederationRequestException>(() => SearchAll(client));

        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        ex.Issues.ShouldBeEmpty();
        ex.Message.ShouldNotContain("secret-body");
        ex.Message.ShouldNotContain("SECRETCT");
    }

    [Fact]
    public async Task Gone_on_a_continuation_is_reported_as_expired_and_is_never_replayed()
    {
        var handler = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct=SECRETCT", Match($"{East}Patient/1", "Patient", "1")))
            .Respond(HttpStatusCode.Gone, OperationOutcome("error", "not-found", "Continuation token expired."));
        using var client = Create(handler);

        var ex = await Should.ThrowAsync<SearchExpiredException>(() => SearchAll(client));

        ex.StatusCode.ShouldBe(HttpStatusCode.Gone);
        ex.Message.ShouldContain("restart", Case.Insensitive);
        ex.Message.ShouldNotContain("SECRETCT");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Redirects_are_errors_and_the_location_is_not_followed()
    {
        var handler = new ScriptedHandler().Respond(HttpStatusCode.Found, "", location: "https://evil.example/steal");
        using var client = Create(handler);

        var ex = await Should.ThrowAsync<FederationRequestException>(() => SearchAll(client));

        ex.StatusCode.ShouldBe(HttpStatusCode.Found);
        ex.Message.ShouldNotContain("evil.example");
        handler.Requests.Count.ShouldBe(1);
    }
}

using System.Diagnostics.CodeAnalysis;
using Shouldly;
using static Ignixa.Federation.SampleClient.UnitTests.TestBundles;
using static Ignixa.Federation.SampleClient.UnitTests.TestClients;

namespace Ignixa.Federation.SampleClient.UnitTests;

/// <summary>Credential confinement: a token only ever travels to the endpoint it belongs to.</summary>
public class CredentialConfinementTests
{
    [Theory]
    [InlineData("https://other.example/fhir/Patient?ct=SECRETCT")]
    [InlineData("http://fed.example/fhir/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example:8443/fhir/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhirx/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/other/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/../other/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/..%2Fother/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/..%5Cother/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/Patient/%2e%2e%2f%2e%2e%2fadmin?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/..%252Fother/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/%252e%252e/other?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/..;/other/Patient?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/%2e%2e%252fother?ct=SECRETCT")]
    [InlineData("https://fed.example/fhir/%2E%2E;/other?ct=SECRETCT")]
    [InlineData("https://user:hunter2@fed.example/fhir/Patient?ct=SECRETCT")]
    public async Task A_next_link_outside_the_federation_scope_is_rejected_before_sending(string next)
    {
        var handler = new ScriptedHandler().Ok(Page(next));
        using var client = Create(handler);

        var ex = await Should.ThrowAsync<LinkRejectedException>(() => SearchAll(client));

        handler.Requests.Count.ShouldBe(1);
        ex.Message.ShouldNotContain("SECRETCT");
        ex.Message.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task A_next_link_in_scope_is_accepted_with_explicit_default_port_and_any_host_case()
    {
        var handler = new ScriptedHandler()
            .Ok(Page("https://FED.example:443/fhir/Patient?ct=a"))
            .Ok(Page());
        using var client = Create(handler);

        await SearchAll(client);

        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Federation_token_is_sent_on_every_federation_request()
    {
        var handler = new ScriptedHandler().Ok(Page($"{Fed}Patient?ct=a")).Ok(Page());
        using var client = Create(handler);

        await SearchAll(client);

        handler.Requests.ShouldAllBe(r => r.Authorization == $"Bearer {FederationToken}");
    }

    [Fact]
    public async Task Anonymous_federation_requests_carry_no_authorization_header()
    {
        var handler = new ScriptedHandler().Ok(Page());
        using var client = Create(handler, token: null);

        await SearchAll(client);

        handler.Requests.ShouldHaveSingleItem().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Read_via_alternate_uses_the_federation_credential()
    {
        var alternate = $"{Fed}shards/k7q2/Patient/1";
        var handler = new ScriptedHandler()
            .Ok(Page(null, Match($"{East}Patient/1", "Patient", "1", alternate: alternate)))
            .Ok("""{"resourceType":"Patient","id":"1"}""");
        using var client = Create(handler);
        var entry = (await SearchAll(client)).Matches.Single();

        using var doc = await client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken);

        doc.RootElement.GetProperty("id").GetString().ShouldBe("1");
        var read = handler.Requests[1];
        read.Uri.AbsoluteUri.ShouldBe(alternate);
        read.Authorization.ShouldBe($"Bearer {FederationToken}");
    }

    [Fact]
    public async Task Read_via_alternate_rejects_an_alternate_outside_the_federation_before_sending()
    {
        var handler = new ScriptedHandler().Ok(Page(null,
            Match($"{East}Patient/1", "Patient", "1", alternate: "https://other.example/fhir/shards/k7q2/Patient/1")));
        using var client = Create(handler);
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken));

        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("https://fed.example/fhir/..%2Fother/shards/k7q2/Patient/1")]
    [InlineData("https://fed.example/fhir/shards/..%5C..%5Cadmin/Patient/1")]
    [InlineData("https://fed.example/fhir/..%252Fother/shards/k7q2/Patient/1")]
    [InlineData("https://fed.example/fhir/shards/..;/admin/Patient/1")]
    public async Task Read_via_alternate_rejects_an_encoded_separator_that_escapes_the_federation_path(string alternate)
    {
        var handler = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/1", "Patient", "1", alternate: alternate)));
        using var client = Create(handler);
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken));

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_next_link_whose_query_holds_encoded_separators_is_still_followed()
    {
        var next = $"{Fed}Patient?ct=a%2Fb%5C..";
        var handler = new ScriptedHandler().Ok(Page(next)).Ok(Page());
        using var client = Create(handler);

        await SearchAll(client);

        handler.Requests[1].Uri.AbsoluteUri.ShouldBe(next);
    }

    [Fact]
    public async Task An_alternate_with_an_encoded_space_in_a_path_segment_is_still_allowed()
    {
        var alternate = $"{Fed}shards/k7q2/Patient/a%20b";
        var handler = new ScriptedHandler()
            .Ok(Page(null, Match($"{East}Patient/a%20b", "Patient", "a b", alternate: alternate)))
            .Ok("""{"resourceType":"Patient","id":"a b"}""");
        using var client = Create(handler);
        var entry = (await SearchAll(client)).Matches.Single();

        using var doc = await client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken);

        handler.Requests[1].Uri.AbsoluteUri.ShouldBe(alternate);
    }

    [Fact]
    public async Task A_direct_read_with_an_encoded_space_in_a_path_segment_is_still_allowed()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/a%20b", "Patient", "a b")));
        var east = new ScriptedHandler().Ok("""{"resourceType":"Patient","id":"a b"}""");
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-east.example"] = east };
        using var client = CreateWithShards(federation, shards, Shard(East, ShardToken));
        var entry = (await SearchAll(client)).Matches.Single();

        using var doc = await client.ReadDirectAsync(entry, TestContext.Current.CancellationToken);

        east.Requests.ShouldHaveSingleItem().Uri.AbsoluteUri.ShouldBe($"{East}Patient/a%20b");
    }

    [Theory]
    [InlineData("..%252Fadmin/Patient/1")]
    [InlineData("..;/admin/Patient/1")]
    public async Task Direct_read_rejects_double_encoded_and_path_parameter_traversal_before_sending(string escape)
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}{escape}", "Patient", "1")));
        var east = new ScriptedHandler();
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-east.example"] = east };
        using var client = CreateWithShards(federation, shards, Shard(East, ShardToken));
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadDirectAsync(entry, TestContext.Current.CancellationToken));

        east.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Direct_read_rejects_an_encoded_separator_that_escapes_the_shard_path_before_sending()
    {
        var federation = new ScriptedHandler()
            .Ok(Page(null, Match($"{East}..%2Fadmin/Patient/1", "Patient", "1")));
        var east = new ScriptedHandler();
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-east.example"] = east };
        using var client = CreateWithShards(federation, shards, Shard(East, ShardToken));
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadDirectAsync(entry, TestContext.Current.CancellationToken));

        east.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_relative_link_is_rejected_with_a_link_rejection_not_a_crash()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/1", "Patient", "1")));
        using var client = Create(federation);
        var relative = new Uri("Patient/1?ct=SECRETCT", UriKind.Relative);
        var entry = new FederatedEntry
        {
            FullUrl = relative,
            Alternate = relative,
            ResourceType = "Patient",
            Id = "1",
            Mode = EntryMode.Match,
            Resource = default,
        };

        var direct = await Should.ThrowAsync<LinkRejectedException>(() => client.ReadDirectAsync(entry, TestContext.Current.CancellationToken));
        var viaFederation = await Should.ThrowAsync<LinkRejectedException>(() => client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken));

        direct.Message.ShouldNotContain("SECRETCT");
        viaFederation.Message.ShouldNotContain("SECRETCT");
        federation.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Read_via_alternate_requires_an_alternate_link()
    {
        var handler = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/1", "Patient", "1")));
        using var client = Create(handler);
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<InvalidOperationException>(() => client.ReadViaFederationAsync(entry, TestContext.Current.CancellationToken));

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Direct_read_goes_to_the_shard_handler_with_the_shard_token_only()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/1", "Patient", "1")));
        var east = new ScriptedHandler().Ok("""{"resourceType":"Patient","id":"1"}""");
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-east.example"] = east };
        using var client = CreateWithShards(federation, shards, Shard(East, ShardToken));
        var entry = (await SearchAll(client)).Matches.Single();

        using var doc = await client.ReadDirectAsync(entry, TestContext.Current.CancellationToken);

        doc.RootElement.GetProperty("id").GetString().ShouldBe("1");
        federation.Requests.Count.ShouldBe(1);
        var read = east.Requests.ShouldHaveSingleItem();
        read.Uri.AbsoluteUri.ShouldBe($"{East}Patient/1");
        read.Authorization.ShouldBe($"Bearer {ShardToken}");
    }

    [Fact]
    public async Task Direct_read_from_a_shard_without_a_token_is_anonymous_not_the_federation_identity()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{West}Patient/1", "Patient", "1")));
        var west = new ScriptedHandler().Ok("""{"resourceType":"Patient","id":"1"}""");
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-west.example"] = west };
        using var client = CreateWithShards(federation, shards, Shard(West, token: null));
        var entry = (await SearchAll(client)).Matches.Single();

        using var doc = await client.ReadDirectAsync(entry, TestContext.Current.CancellationToken);

        west.Requests.ShouldHaveSingleItem().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Direct_read_of_a_full_url_outside_every_configured_shard_is_rejected_before_sending()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{West}Patient/1", "Patient", "1")));
        var east = new ScriptedHandler();
        var shards = new Dictionary<string, ScriptedHandler> { ["shard-east.example"] = east };
        using var client = CreateWithShards(federation, shards, Shard(East, ShardToken));
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadDirectAsync(entry, TestContext.Current.CancellationToken));

        east.Requests.ShouldBeEmpty();
        federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Direct_read_is_unavailable_when_no_shards_are_configured()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/1", "Patient", "1")));
        using var client = Create(federation);
        var entry = (await SearchAll(client)).Matches.Single();

        await Should.ThrowAsync<LinkRejectedException>(() => client.ReadDirectAsync(entry, TestContext.Current.CancellationToken));

        federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public void Redirect_following_handlers_are_rejected_at_construction()
    {
        var options = new FederationClientOptions { FederationBaseUrl = new Uri(Fed) };

        Should.Throw<ArgumentException>(() => new FederationClient(options, new HttpClientHandler()));
        Should.Throw<ArgumentException>(() =>
            new FederationClient(options, new SocketsHttpHandler { AllowAutoRedirect = true }));

        var withShard = new FederationClientOptions
        {
            FederationBaseUrl = new Uri(Fed),
            DirectReadShards = [Shard(East, null)],
        };
        Should.Throw<ArgumentException>(() =>
            new FederationClient(withShard, new ScriptedHandler(), _ => new HttpClientHandler()));
    }

    [Fact]
    public void Handlers_with_redirects_disabled_and_the_default_handlers_are_accepted()
    {
        var options = new FederationClientOptions { FederationBaseUrl = new Uri(Fed), DirectReadShards = [Shard(East, null)] };

        new FederationClient(options, new SocketsHttpHandler { AllowAutoRedirect = false }).Dispose();
        new FederationClient(options).Dispose();
    }

    [Theory]
    [InlineData("http://fed.example/fhir/")]
    [InlineData("https://user:pw@fed.example/fhir/")]
    [InlineData("https://fed.example/fhir/?x=1")]
    [InlineData("https://fed.example/fhir/#frag")]
    [SuppressMessage(
        "Design",
        "CA1054:URI parameters should not be strings",
        Justification = "InlineData carries only constants, so the URL arrives as a string and is parsed inside the test.")]
    public void Base_urls_must_be_plain_https(string baseUrl)
    {
        Should.Throw<ArgumentException>(() => new FederationClient(
            new FederationClientOptions { FederationBaseUrl = new Uri(baseUrl) }, new ScriptedHandler()));
        Should.Throw<ArgumentException>(() => new FederationClient(
            new FederationClientOptions { FederationBaseUrl = new Uri(Fed), DirectReadShards = [Shard(baseUrl, null)] },
            new ScriptedHandler(),
            _ => new ScriptedHandler()));
    }

    [Fact]
    public async Task Base_url_without_trailing_slash_is_normalised()
    {
        var handler = new ScriptedHandler().Ok(Page());
        using var client = Create(handler, baseUrl: "https://fed.example/fhir");

        await SearchAll(client);

        handler.Requests.ShouldHaveSingleItem().Uri.AbsoluteUri.ShouldBe($"{Fed}Patient");
    }

    [Fact]
    public void Overlapping_endpoint_scopes_are_rejected_so_a_url_never_matches_two_credentials()
    {
        var options = new FederationClientOptions
        {
            FederationBaseUrl = new Uri(Fed),
            DirectReadShards = [Shard(East, "a"), Shard("https://shard-east.example/fhir/sub/", "b")],
        };

        Should.Throw<ArgumentException>(() => new FederationClient(options, new ScriptedHandler(), _ => new ScriptedHandler()));
    }

    [Fact]
    public void A_shard_base_under_the_federation_base_is_rejected()
    {
        var options = new FederationClientOptions
        {
            FederationBaseUrl = new Uri("https://fed.example/fhir/"),
            DirectReadShards = [Shard("https://fed.example/fhir/shards/east/", "a")],
        };

        Should.Throw<ArgumentException>(() => new FederationClient(options, new ScriptedHandler(), _ => new ScriptedHandler()));
    }

    [Fact]
    public void The_federation_base_under_a_shard_base_is_rejected()
    {
        var options = new FederationClientOptions
        {
            FederationBaseUrl = new Uri("https://shard-east.example/fhir/federation/"),
            DirectReadShards = [Shard(East, "a")],
        };

        Should.Throw<ArgumentException>(() => new FederationClient(options, new ScriptedHandler(), _ => new ScriptedHandler()));
    }

    [Fact]
    public void The_default_transport_does_not_follow_redirects()
    {
        using var handler = FederationClient.CreateDefaultHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
    }
}

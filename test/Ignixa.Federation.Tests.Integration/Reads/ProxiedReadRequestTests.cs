using Ignixa.Federation.Tests.Integration.Authentication;
using Ignixa.Federation.Tests.Integration.Search;
using Shouldly;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>Who may read and what a read may carry: authentication and type grants first, then a validated owner, type and id.</summary>
public sealed class ProxiedReadRequestTests
{
    private const string Dup = "/shards/k7q2/Patient/dup";

    [Fact]
    public async Task MissingOrUntrustedToken_IsChallenged_WithoutBindingIo_AndWithoutRevealingTheAlias()
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);
        string untrusted = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], UntrustedSigner = true });

        ReadResponse bound = await ReadResponse.GetAsync(host, Dup, token: null);
        ReadResponse unknown = await ReadResponse.GetAsync(host, "/shards/zzzz/Patient/dup", token: null);
        ReadResponse bad = await ReadResponse.GetAsync(host, Dup, untrusted);

        foreach (ReadResponse denied in new[] { bound, unknown, bad })
        {
            denied.Status.ShouldBe(401);
            denied.IssueCode.ShouldBe("login");
            denied.IsCleanFailure.ShouldBeTrue();
            denied.Headers["WWW-Authenticate"].ShouldStartWith("Bearer");
        }

        bound.Body.ShouldBe(unknown.Body);
        binding.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/shards/k7q2/Patient/dup")]
    [InlineData("/shards/zzzz/Patient/dup")]
    [InlineData("/shards/k7q2/Practitioner/dup")]
    public async Task RoleWithoutTheType_IsForbidden_WithAStandardBody_AndNoBindingIo(string path)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole] });

        ReadResponse denied = await ReadResponse.GetAsync(host, path, token);

        denied.Status.ShouldBe(403);
        denied.IssueCode.ShouldBe("forbidden");
        denied.IsCleanFailure.ShouldBeTrue();
        denied.Body.ShouldNotContain("k7q2");
        denied.Body.ShouldNotContain("east");
        denied.Body.ShouldNotContain("fhir-east");
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeniedCallers_SeeTheSameBody_ForBoundAndUnboundAliases()
    {
        using var host = ProxiedReadTests.Start(new ScriptedReadBinding());
        string token = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole] });

        ReadResponse bound = await ReadResponse.GetAsync(host, Dup, token);
        ReadResponse unknown = await ReadResponse.GetAsync(host, "/shards/zzzz/Patient/dup", token);

        bound.Body.ShouldBe(unknown.Body);
    }

    [Fact]
    public async Task SmartScopesAndNoRoles_AreForbidden_WithoutBindingIo()
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);
        string scoped = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ReaderRole], Scp = "patient/*.read" });
        string noRoles = host.Token(new SyntheticTokenRequest());

        (await ReadResponse.GetAsync(host, Dup, scoped)).Status.ShouldBe(403);
        (await ReadResponse.GetAsync(host, Dup, noRoles)).Status.ShouldBe(403);
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisabledHost_AuthenticatesFirst_ThenAnswersNotFound_WithoutBindingIo()
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding, setup => setup.Settings["Federation:ProxyReads"] = "false");
        string denied = host.Token(new SyntheticTokenRequest { Roles = [SearchWorld.ObservationRole] });

        (await ReadResponse.GetAsync(host, Dup, token: null)).Status.ShouldBe(401);
        (await ReadResponse.GetAsync(host, Dup, denied)).Status.ShouldBe(403);
        ReadResponse disabled = await ReadResponse.GetAsync(host, Dup, host.Token());

        disabled.Status.ShouldBe(404);
        disabled.IssueCode.ShouldBe("not-found");
        disabled.IsCleanFailure.ShouldBeTrue();
        binding.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/Patient/dup", false)]
    [InlineData("/Observation/obs-dup", false)]
    [InlineData("/Practitioner/anyone", false)]
    [InlineData("/Patient/dup", true)]
    public async Task UnqualifiedTypedRead_IsAlwaysAmbiguous_AndPointsAtTheFullUrlOrProxy_WithoutLookup(string path, bool authenticated)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, path, authenticated ? host.Token() : null);

        result.Status.ShouldBe(400);
        result.IssueCode.ShouldBe("invalid");
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldContain("fullUrl");
        result.Body.ShouldContain("/shards/");
        binding.Calls.ShouldBeEmpty();
        host.Shards.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnqualifiedTypedRead_MentionsTheProxyOnlyWhenItIsEnabled(bool bound, bool mentionsProxy)
    {
        using var host = ProxiedReadTests.Start(bound ? new ScriptedReadBinding() : null);

        ReadResponse result = await ReadResponse.GetAsync(host, "/Patient/dup", token: null);

        result.Status.ShouldBe(400);
        result.Body.ShouldContain("fullUrl");
        result.Body.Contains("/shards/", StringComparison.Ordinal).ShouldBe(mentionsProxy);
    }

    [Fact]
    public async Task UnqualifiedTypedRead_IsAmbiguous_OnAnUnconfiguredHostToo()
    {
        using var host = ProxiedReadTests.Start(new ScriptedReadBinding(), setup => setup.Unconfigure());

        ReadResponse result = await ReadResponse.GetAsync(host, "/Patient/dup", token: null);

        result.Status.ShouldBe(400);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a%2Fb")]
    [InlineData("%2e%2e")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a_b")]
    public async Task UnsafeOrUnparsableIds_AreRefused_BeforeAnyBindingCall(string id)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        // The raw path reaches the application as given, so these ids arrive exactly as written.
        ReadResponse result = await ReadResponse.GetRawAsync(host, "/shards/k7q2/Patient/" + id, host.Token(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(400);
        result.IssueCode.ShouldBe("invalid");
        result.IsCleanFailure.ShouldBeTrue();
        binding.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnIdOverTheCoreLimit_IsRefused_BeforeAnyBindingCall()
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, "/shards/k7q2/Patient/" + new string('a', 65), host.Token());

        result.Status.ShouldBe(400);
        binding.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/shards/k7q2/Patient/dup/extra")]
    [InlineData("/shards/k7q2/patient/dup")]
    [InlineData("/shards/k7q2/Patient")]
    [InlineData("/shards/k7q2")]
    public async Task OtherShapes_AreNotReadRoutes(string path)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, path, host.Token());

        result.Status.ShouldBe(404);
        binding.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("application%2Fjson")]
    [InlineData("application%2Ffhir%2Bjson")]
    public async Task JsonFormats_AreServed_AndNothingButTheKeyReachesTheBinding(string format)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup + "?_format=" + format, host.Token());

        result.Status.ShouldBe(200);
        binding.Calls.Count.ShouldBe(1);
        binding.Calls[0].Key.Id.Value.ShouldBe("dup");
    }

    [Theory]
    [InlineData("?_format=xml", 415)]
    [InlineData("?_format=application%2Ffhir%2Bxml", 415)]
    [InlineData("?_format=", 415)]
    [InlineData("?_format=json&_format=json", 400)]
    [InlineData("?_pretty=true", 400)]
    [InlineData("?_summary=text", 400)]
    [InlineData("?_elements=id", 400)]
    [InlineData("?secret=1", 400)]
    [InlineData("?_format=json&x=1", 400)]
    public async Task UnsupportedFormatsAndAnyOtherQuery_AreRefused_WithoutBindingIo(string query, int status)
    {
        var binding = new ScriptedReadBinding();
        using var host = ProxiedReadTests.Start(binding);

        ReadResponse result = await ReadResponse.GetAsync(host, Dup + query, host.Token());

        result.Status.ShouldBe(status);
        result.IsCleanFailure.ShouldBeTrue();
        result.Body.ShouldNotContain("secret");
        binding.Calls.ShouldBeEmpty();
    }
}

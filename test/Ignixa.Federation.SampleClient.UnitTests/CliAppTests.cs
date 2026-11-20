using System.Net;
using Shouldly;
using static Ignixa.Federation.SampleClient.UnitTests.TestBundles;
using static Ignixa.Federation.SampleClient.UnitTests.TestClients;

namespace Ignixa.Federation.SampleClient.UnitTests;

/// <summary>
/// The command-line front end, run end to end with scripted handlers. Every run also checks the one rule that holds
/// for all outcomes: no token and no continuation value ever reaches stdout or stderr.
/// </summary>
public class CliAppTests
{
    private const string Ct = "SECRETCT%2Bab%3D";
    private static readonly string[] SecretsThatMustNeverBePrinted = [FederationToken, ShardToken, "SECRETCT", "ct="];

    private static readonly Dictionary<string, string?> BothTokens = new()
    {
        ["FEDERATION_ACCESS_TOKEN"] = FederationToken,
        ["SHARD_ACCESS_TOKEN"] = ShardToken,
    };

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr, ScriptedHandler Federation, Dictionary<string, ScriptedHandler> Shards);

    private static async Task<CliRun> RunAsync(
        string[] args, ScriptedHandler? federation = null, IReadOnlyDictionary<string, string?>? env = null)
    {
        federation ??= new ScriptedHandler();
        Dictionary<string, ScriptedHandler> shards = new()
        {
            ["shard-east.example"] = new ScriptedHandler(),
            ["shard-west.example"] = new ScriptedHandler(),
        };
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        var exitCode = await CliApp.RunAsync(args, env ?? BothTokens, stdout, stderr, federation, shardBase => shards[shardBase.Host]);

        var run = new CliRun(exitCode, stdout.ToString(), stderr.ToString(), federation, shards);
        foreach (var secret in SecretsThatMustNeverBePrinted)
        {
            run.Stdout.ShouldNotContain(secret);
            run.Stderr.ShouldNotContain(secret);
        }

        return run;
    }

    private static ScriptedHandler TwoPages() => new ScriptedHandler()
        .Ok(Page($"{Fed}Patient?ct={Ct}",
            Match($"{East}Patient/123", "Patient", "123", shard: "k7q2", alternate: $"{Fed}shards/k7q2/Patient/123"),
            Include($"{East}Organization/9", "Organization", "9", shard: "k7q2")))
        .Ok(Page(null,
            Match($"{West}Patient/123", "Patient", "123", shard: "p9xm", alternate: $"{Fed}shards/p9xm/Patient/123"),
            Include($"{East}Organization/9", "Organization", "9", shard: "k7q2"),
            OutcomeEntry("warning", "incomplete", "included resources truncated", shard: "p9xm")));

    [Fact]
    public async Task Searching_two_pages_prints_each_entry_by_full_url_and_a_distinct_summary()
    {
        var run = await RunAsync(
            ["--base", Fed, "--type", "Patient", "--param", "name=smith", "--param", "_include=Patient:organization"],
            TwoPages());

        run.ExitCode.ShouldBe(0);
        var lines = run.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.ShouldContain("page 1: matches=1 includes=1 outcomes=0");
        lines.ShouldContain("page 2: matches=1 includes=1 outcomes=1");
        lines.ShouldContain($"match  {East}Patient/123  (shard=k7q2)");
        lines.ShouldContain($"match  {West}Patient/123  (shard=p9xm)");
        lines.ShouldContain($"include  {East}Organization/9  (shard=k7q2)");
        lines.ShouldContain("outcome warning/incomplete: included resources truncated (shard=p9xm)");
        lines.ShouldContain("summary: pages=2 matches=2 includes=1 outcomes=1");
        run.Stdout.ShouldNotContain("more pages");

        run.Federation.Requests.Count.ShouldBe(2);
        run.Federation.Requests[0].Uri.ShouldBe(new Uri($"{Fed}Patient?name=smith&_include=Patient%3Aorganization"));
        run.Federation.Requests.ShouldAllBe(r => r.Authorization == $"Bearer {FederationToken}");
    }

    [Fact]
    public async Task Stopping_at_max_pages_says_more_pages_exist_without_fetching_them()
    {
        var run = await RunAsync(["--base", Fed, "--type", "Patient", "--max-pages", "1"], TwoPages());

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain("more pages are available");
        run.Stdout.ShouldContain("summary: pages=1 matches=1 includes=1 outcomes=0");
        run.Federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Base_url_can_come_from_the_environment_and_a_missing_token_means_anonymous()
    {
        var federation = new ScriptedHandler().Ok(Page());

        var run = await RunAsync(
            ["--type", "Patient"], federation, new Dictionary<string, string?> { ["FEDERATION_BASE_URL"] = Fed });

        run.ExitCode.ShouldBe(0);
        federation.Requests.ShouldHaveSingleItem().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Read_alternate_uses_the_federation_proxied_route_with_the_federation_token()
    {
        var federation = TwoPages().Ok(new System.Text.Json.Nodes.JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = "123",
            ["name"] = "RESOURCEBODY",
        }.ToJsonString());

        var run = await RunAsync(["--base", Fed, "--type", "Patient", "--read", "alternate"], federation);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain($"Patient/123 read via alternate from {Fed}shards/k7q2/Patient/123");
        run.Stdout.ShouldNotContain("RESOURCEBODY");
        federation.Requests.Count.ShouldBe(3);
        federation.Requests[2].Uri.ShouldBe(new Uri($"{Fed}shards/k7q2/Patient/123"));
        federation.Requests[2].Authorization.ShouldBe($"Bearer {FederationToken}");
        run.Shards.Values.ShouldAllBe(shard => shard.Requests.Count == 0);
    }

    [Fact]
    public async Task Read_direct_goes_to_the_shard_with_the_shard_token_and_never_the_federation_token()
    {
        var federation = new ScriptedHandler().Ok(Page(null,
            Match($"{East}Patient/123", "Patient", "123", shard: "k7q2", alternate: $"{Fed}shards/k7q2/Patient/123")));
        var shardHandlers = new Dictionary<string, ScriptedHandler>
        {
            ["shard-east.example"] = new ScriptedHandler().Ok(
                """{"resourceType":"Patient","id":"123","name":"RESOURCEBODY"}"""),
        };
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        var exitCode = await CliApp.RunAsync(
            ["--base", Fed, "--type", "Patient", "--read", "direct", "--direct-shard", East],
            BothTokens, stdout, stderr, federation, shardBase => shardHandlers[shardBase.Host]);

        exitCode.ShouldBe(0);
        stdout.ToString().ShouldContain($"Patient/123 read via direct from {East}Patient/123");
        stdout.ToString().ShouldNotContain("RESOURCEBODY");
        federation.Requests.Count.ShouldBe(1);
        var shardRequest = shardHandlers["shard-east.example"].Requests.ShouldHaveSingleItem();
        shardRequest.Uri.ShouldBe(new Uri($"{East}Patient/123"));
        shardRequest.Authorization.ShouldBe($"Bearer {ShardToken}");
        (stdout.ToString() + stderr).ShouldNotContain(FederationToken);
        (stdout.ToString() + stderr).ShouldNotContain(ShardToken);
    }

    [Fact]
    public async Task Read_direct_without_a_configured_shard_is_rejected_before_sending()
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/123", "Patient", "123")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient", "--read", "direct"], federation);

        run.ExitCode.ShouldBe(5);
        run.Stderr.ShouldContain("No request was sent");
        run.Shards.Values.ShouldAllBe(shard => shard.Requests.Count == 0);
        federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_continuation_exits_3_with_restart_guidance_and_is_not_replayed()
    {
        var federation = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct={Ct}", Match($"{East}Patient/1", "Patient", "1")))
            .Respond(HttpStatusCode.Gone, OperationOutcome("error", "not-found", "continuation expired"));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(3);
        run.Stderr.ShouldContain("expired");
        run.Stderr.ShouldContain("restart the search from the original query");
        run.Stderr.ShouldContain("nothing was replayed");
        federation.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_server_error_exits_4_with_the_status_and_parsed_issues_only()
    {
        var federation = new ScriptedHandler().Respond(
            HttpStatusCode.InternalServerError, OperationOutcome("fatal", "exception", "shard exploded"));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("HTTP 500");
        run.Stderr.ShouldContain("fatal/exception: shard exploded");
        run.Stderr.ShouldNotContain("resourceType");
    }

    [Fact]
    public async Task A_next_link_outside_the_federation_exits_5_and_sends_nothing_more()
    {
        var federation = new ScriptedHandler()
            .Ok(Page($"https://other.example/fhir/Patient?ct={Ct}", Match($"{East}Patient/1", "Patient", "1")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(5);
        run.Stderr.ShouldContain("Refusing to follow a link");
        run.Stderr.ShouldContain("No request was sent");
        federation.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("--type", "Patient", "--base", "http://fed.example/fhir/")]
    [InlineData("--base", Fed)]
    [InlineData("--base", Fed, "--type", "Patient", "--max-pages", "0")]
    [InlineData("--base", Fed, "--type", "Patient", "--max-pages", "many")]
    [InlineData("--base", Fed, "--type", "Patient", "--read", "sideways")]
    [InlineData("--base", Fed, "--type", "Patient", "--param", "no-equals-sign")]
    [InlineData("--base", Fed, "--type", "Patient", "--param", "ct=anything")]
    [InlineData("--base", Fed, "--type", "Patient", "--direct-shard", "http://shard-east.example/fhir/")]
    [InlineData("--base", Fed, "--type", "Patient", "--bogus")]
    [InlineData("--base", Fed, "--type", "Patient", "--base")]
    public async Task Usage_errors_exit_2_and_send_nothing(params string[] args)
    {
        var federation = new ScriptedHandler();

        var run = await RunAsync(args, federation);

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain("error:");
        federation.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--token", "fake-fed-token")]
    [InlineData("--token=fake-fed-token")]
    [InlineData("--access-token", "fake-shard-token")]
    [InlineData("--shard-token", "fake-shard-token")]
    [InlineData("--bearer", "fake-fed-token")]
    public async Task A_credential_passed_as_an_argument_is_refused_and_points_to_the_environment(params string[] credentialArgs)
    {
        var federation = new ScriptedHandler();

        var run = await RunAsync(["--base", Fed, "--type", "Patient", .. credentialArgs], federation);

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain("FEDERATION_ACCESS_TOKEN");
        run.Stderr.ShouldContain("SHARD_ACCESS_TOKEN");
        federation.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Help_prints_usage_to_stdout_and_exits_0()
    {
        var run = await RunAsync(["--help"]);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain("Usage:");
        run.Stdout.ShouldContain("FEDERATION_ACCESS_TOKEN");
        run.Stderr.ShouldBeEmpty();
    }
}

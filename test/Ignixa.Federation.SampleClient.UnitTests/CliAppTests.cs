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
    // Spec-sized: a real ct is an encrypted payload of well over 120 characters, so it is always long enough to be truncated.
    private static readonly string LongCt = string.Concat(Enumerable.Repeat("Zm9vYmFyQkFTRTY0VVJMLV9zYWZl", 11));

    // A leaked prefix of 16 characters counts as a leak, not only the whole continuation value.
    private static readonly string[] SecretsThatMustNeverBePrinted =
        [FederationToken, ShardToken, "SECRETCT", "ct=", LongCt[..16]];

    private static readonly Dictionary<string, string?> BothTokens = new()
    {
        ["FEDERATION_ACCESS_TOKEN"] = FederationToken,
        ["SHARD_ACCESS_TOKEN"] = ShardToken,
    };

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr, ScriptedHandler Federation, Dictionary<string, ScriptedHandler> Shards);

    private static async Task<CliRun> RunAsync(
        string[] args,
        ScriptedHandler? federation = null,
        IReadOnlyDictionary<string, string?>? env = null,
        Dictionary<string, ScriptedHandler>? shards = null)
    {
        federation ??= new ScriptedHandler();
        shards ??= new()
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

        var run = await RunAsync(
            ["--base", Fed, "--type", "Patient", "--read", "direct", "--direct-shard", East], federation, shards: shardHandlers);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain($"Patient/123 read via direct from {East}Patient/123");
        run.Stdout.ShouldNotContain("RESOURCEBODY");
        federation.Requests.Count.ShouldBe(1);
        var shardRequest = shardHandlers["shard-east.example"].Requests.ShouldHaveSingleItem();
        shardRequest.Uri.ShouldBe(new Uri($"{East}Patient/123"));
        shardRequest.Authorization.ShouldBe($"Bearer {ShardToken}");
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
    [InlineData("must be absolute https", "--type", "Patient", "--base", "http://fed.example/fhir/")]
    [InlineData("--type is required", "--base", Fed)]
    [InlineData("--max-pages must be a whole number", "--base", Fed, "--type", "Patient", "--max-pages", "0")]
    [InlineData("--max-pages must be a whole number", "--base", Fed, "--type", "Patient", "--max-pages", "many")]
    [InlineData("--read must be none, alternate or direct", "--base", Fed, "--type", "Patient", "--read", "sideways")]
    [InlineData("--read must be none, alternate or direct", "--base", Fed, "--type", "Patient", "--read", "1")]
    [InlineData("--read must be none, alternate or direct", "--base", Fed, "--type", "Patient", "--read", "none,alternate")]
    [InlineData("--param must look like name=value", "--base", Fed, "--type", "Patient", "--param", "no-equals-sign")]
    [InlineData("'ct' parameter is issued by the federation", "--base", Fed, "--type", "Patient", "--param", "ct=anything")]
    [InlineData("--direct-shard must be an absolute https URL", "--base", Fed, "--type", "Patient", "--direct-shard", "not-a-url")]
    [InlineData("must be absolute https", "--base", Fed, "--type", "Patient", "--direct-shard", "http://shard-east.example/fhir/")]
    [InlineData("unknown option --bogus", "--base", Fed, "--type", "Patient", "--bogus", "x")]
    [InlineData("--base needs a value", "--type", "Patient", "--base")]
    [InlineData("--base needs a value", "--base", "--type", "Patient")]
    [InlineData("--type needs a value", "--base", Fed, "--type", "--param", "a=b")]
    [InlineData("unexpected argument", "--base", Fed, "--type", "Patient", "stray")]
    [InlineData("unexpected argument", "Patient")]
    [InlineData("letters only", "--base", Fed, "--type", "-h")]
    public async Task Usage_errors_exit_2_with_the_reason_and_send_nothing(string expectedFragment, params string[] args)
    {
        var federation = new ScriptedHandler();

        var run = await RunAsync(args, federation);

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain($"error: ");
        run.Stderr.ShouldContain(expectedFragment);
        run.Stderr.ShouldContain("Run with --help for usage.");
        federation.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_base_argument_or_environment_variable_it_is_a_usage_error()
    {
        var run = await RunAsync(["--type", "Patient"], env: new Dictionary<string, string?>());

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain("pass --base or set FEDERATION_BASE_URL");
    }

    [Fact]
    public async Task The_base_argument_overrides_the_environment_variable()
    {
        var federation = new ScriptedHandler().Ok(Page());
        var env = new Dictionary<string, string?> { ["FEDERATION_BASE_URL"] = "https://other.example/fhir/" };

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation, env);

        run.ExitCode.ShouldBe(0);
        federation.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri($"{Fed}Patient"));
    }

    [Theory]
    [InlineData("--token", "fake-fed-token")]
    [InlineData("--token=fake-fed-token")]
    [InlineData("--access-token", "fake-shard-token")]
    [InlineData("--shard-token", "fake-shard-token")]
    [InlineData("--bearer", "fake-fed-token")]
    [InlineData("--token", "fake-fed-token", "--help")]
    public async Task A_credential_passed_as_an_argument_is_refused_and_points_to_the_environment(params string[] credentialArgs)
    {
        var federation = new ScriptedHandler();

        var run = await RunAsync(["--base", Fed, "--type", "Patient", .. credentialArgs], federation);

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain("FEDERATION_ACCESS_TOKEN");
        run.Stderr.ShouldContain("SHARD_ACCESS_TOKEN");
        federation.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--base", Fed, "--type", "Patient", "--help")]
    public async Task Help_prints_usage_to_stdout_and_exits_0(params string[] args)
    {
        var federation = new ScriptedHandler();

        var run = await RunAsync(args, federation);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain("Usage:");
        run.Stdout.ShouldContain("FEDERATION_ACCESS_TOKEN");
        run.Stdout.ShouldContain("the first match has no alternate link");
        run.Stderr.ShouldBeEmpty();
        federation.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("alternate")]
    [InlineData("ALTERNATE")]
    public async Task Read_alternate_without_an_alternate_link_exits_5_and_sends_no_read(string mode)
    {
        var federation = new ScriptedHandler().Ok(Page(null, Match($"{East}Patient/123", "Patient", "123", shard: "k7q2")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient", "--read", mode], federation);

        run.ExitCode.ShouldBe(5);
        run.Stderr.ShouldContain("no alternate link");
        run.Stderr.ShouldContain("No request was sent");
        federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_timeout_exits_4_without_a_stack_trace()
    {
        var federation = new ScriptedHandler().Throw(new TaskCanceledException("timed out", new TimeoutException()));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("error: the request timed out.");
        run.Stderr.ShouldNotContain(" at ");
    }

    [Fact]
    public async Task A_response_that_breaks_off_while_being_read_exits_4()
    {
        var federation = new ScriptedHandler().Throw(new HttpIOException(HttpRequestError.ResponseEnded, "the connection closed"));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("error: the response could not be read.");
        run.Stderr.ShouldNotContain("the connection closed");
    }

    [Fact]
    public async Task A_redirect_exits_4_and_is_not_followed()
    {
        var federation = new ScriptedHandler().Respond(HttpStatusCode.Found, "", location: "https://other.example/fhir/Patient");

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("HTTP 302");
        run.Stderr.ShouldContain("Redirects are never followed");
        federation.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_response_that_is_not_json_exits_4_without_quoting_it()
    {
        var federation = new ScriptedHandler().Ok("RESPONSEBODY {not json");

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("error: the response was not a valid FHIR response.");
        run.Stderr.ShouldNotContain("RESPONSEBODY");
    }

    [Fact]
    public async Task A_repeated_next_link_exits_4_and_says_so_rather_than_blaming_the_response_format()
    {
        var federation = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct={Ct}", Match($"{East}Patient/1", "Patient", "1")))
            .Ok(Page($"{Fed}Patient?ct={Ct}", Match($"{East}Patient/2", "Patient", "2")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("next link that was already followed");
        run.Stderr.ShouldNotContain("not a valid FHIR response");
    }

    [Theory]
    [InlineData("FEDERATION_ACCESS_TOKEN", "fake-fed-token\r\nX-Injected: yes")]
    [InlineData("FEDERATION_ACCESS_TOKEN", "fake-fed-token\nX-Injected: yes")]
    [InlineData("SHARD_ACCESS_TOKEN", "fake-shard-token\rX-Injected: yes")]
    public async Task A_token_with_a_line_break_is_a_usage_error_that_never_echoes_the_value(string variable, string value)
    {
        var federation = new ScriptedHandler();
        var env = new Dictionary<string, string?> { [variable] = value };

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation, env);

        run.ExitCode.ShouldBe(2);
        run.Stderr.ShouldContain($"{variable} contains a line break");
        run.Stderr.ShouldNotContain("X-Injected");
        federation.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_token_is_trimmed_of_surrounding_whitespace_before_it_is_sent()
    {
        var federation = new ScriptedHandler().Ok(Page());
        var env = new Dictionary<string, string?> { ["FEDERATION_ACCESS_TOKEN"] = $"  {FederationToken}\r\n" };

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation, env);

        run.ExitCode.ShouldBe(0);
        federation.Requests.ShouldHaveSingleItem().Authorization.ShouldBe($"Bearer {FederationToken}");
    }

    [Fact]
    public async Task A_failure_on_a_continuation_never_prints_the_continuation_value_even_if_the_server_echoes_it()
    {
        var federation = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct={Ct}", Match($"{East}Patient/1", "Patient", "1")))
            .Respond(
                HttpStatusCode.BadRequest,
                OperationOutcome("error", "invalid", "bad continuation SECRETCT%2Bab%3D (decoded: SECRETCT+ab=) in ct=SECRETCT%2Bab%3D"));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(4);
        run.Stderr.ShouldContain("HTTP 400");
        run.Stderr.ShouldContain("error/invalid: bad continuation");
    }

    [Fact]
    public async Task An_outcome_on_a_continuation_page_never_prints_the_continuation_value_even_if_the_server_echoes_it()
    {
        var federation = new ScriptedHandler()
            .Ok(Page($"{Fed}Patient?ct={Ct}", Match($"{East}Patient/1", "Patient", "1")))
            .Ok(Page(null, OutcomeEntry("warning", "incomplete", "truncated after SECRETCT%2Bab%3D")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain("outcome warning/incomplete: truncated after");
    }

    [Fact]
    public Task A_spec_sized_ct_echoed_late_in_a_long_diagnostic_is_hidden_even_after_the_library_truncates_it() =>
        AssertTruncatedEchoIsHiddenAsync(LongCt, filler: 176, echo: $"ct={LongCt} and more");

    [Fact]
    public Task A_decoded_ct_cut_off_mid_value_by_truncation_is_hidden() =>
        AssertTruncatedEchoIsHiddenAsync(Ct, filler: 190, echo: "SECRETCT+ab= and more");

    /// <summary>
    /// The library cuts each diagnostic to 200 characters, so an echo near the end arrives as a prefix only. The
    /// leak check in <see cref="RunAsync"/> fails if the first 16 characters of <c>LongCt</c>, or either literal
    /// <c>SECRETCT</c> or <c>ct=</c>, appears.
    /// </summary>
    private static async Task AssertTruncatedEchoIsHiddenAsync(string ct, int filler, string echo)
    {
        var diagnostics = new string('x', filler) + " " + echo;
        diagnostics.Length.ShouldBeGreaterThan(200);
        var firstPage = Page($"{Fed}Patient?ct={ct}", Match($"{East}Patient/1", "Patient", "1"));

        var failed = await RunAsync(["--base", Fed, "--type", "Patient"], new ScriptedHandler()
            .Ok(firstPage)
            .Respond(HttpStatusCode.BadRequest, OperationOutcome("error", "invalid", diagnostics)));
        var warned = await RunAsync(["--base", Fed, "--type", "Patient"], new ScriptedHandler()
            .Ok(firstPage)
            .Ok(Page(null, OutcomeEntry("warning", "incomplete", diagnostics))));

        failed.ExitCode.ShouldBe(4);
        failed.Stderr.ShouldContain("error/invalid: xxx");
        failed.Stderr.ShouldContain("[hidden]");
        warned.ExitCode.ShouldBe(0);
        warned.Stdout.ShouldContain("outcome warning/incomplete: xxx");
        warned.Stdout.ShouldContain("[hidden]");
    }

    [Fact]
    public async Task Control_characters_in_server_diagnostics_cannot_forge_output_lines()
    {
        var failing = new ScriptedHandler().Respond(
            HttpStatusCode.InternalServerError, OperationOutcome("fatal", "exception", "boom\nsummary: pages=99\r\x1b[2J"));
        var warning = new ScriptedHandler().Ok(
            Page(null, OutcomeEntry("warning", "incomplete", "cut\nsummary: pages=99 matches=99")));

        var failed = await RunAsync(["--base", Fed, "--type", "Patient"], failing);
        var warned = await RunAsync(["--base", Fed, "--type", "Patient"], warning);

        failed.ExitCode.ShouldBe(4);
        failed.Stderr.ShouldContain("fatal/exception: boom summary: pages=99");
        failed.Stderr.ShouldNotContain("\x1b");
        failed.Stderr.Split('\n').ShouldNotContain(line => line.StartsWith("summary:", StringComparison.Ordinal));
        warned.ExitCode.ShouldBe(0);
        warned.Stdout.ShouldContain("outcome warning/incomplete: cut summary: pages=99 matches=99");
        warned.Stdout.Split('\n').Count(line => line.StartsWith("summary:", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task An_ipv6_full_url_is_printed_with_its_brackets_and_port()
    {
        var federation = new ScriptedHandler().Ok(Page(null,
            Match("https://[2001:db8::2]:8443/fhir/Patient/1?secret=1", "Patient", "1")));

        var run = await RunAsync(["--base", Fed, "--type", "Patient"], federation);

        run.ExitCode.ShouldBe(0);
        run.Stdout.ShouldContain("match  https://[2001:db8::2]:8443/fhir/Patient/1");
        run.Stdout.ShouldNotContain("secret");
    }
}

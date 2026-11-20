using System.Text.Json;

namespace Ignixa.Federation.SampleClient;

/// <summary>
/// The command-line front end. Exit codes: 0 ok, 2 usage error, 3 continuation expired, 4 federation or HTTP error,
/// 5 link outside the allowed scope. Output never includes tokens, continuation (<c>next</c>) links or query strings,
/// or resource bodies.
/// </summary>
public static class CliApp
{
    private const string Usage = """
        Usage: Ignixa.Federation.SampleClient --base <https url> --type <ResourceType> [options]

        Options:
          --base <url>            Federation base URL (https), or set FEDERATION_BASE_URL.
          --type <ResourceType>   Resource type to search, for example Patient.
          --param name=value      Search parameter; repeatable.
          --max-pages <n>         Stop after n pages (default 5).
          --read none|alternate|direct
                                  Also read the first match (default none).
                                  alternate: via the federation, with the federation token.
                                  direct: from its shard, only if listed with --direct-shard.
          --direct-shard <url>    A shard (https base URL) that may be read directly; repeatable.
          -h, --help              Show this help.

        Credentials are read from the environment only:
          FEDERATION_ACCESS_TOKEN  Bearer token for the federation (optional).
          SHARD_ACCESS_TOKEN       Bearer token for --direct-shard shards only (optional).

        Exit codes: 0 ok, 2 usage error, 3 continuation expired (restart the search),
        4 federation or HTTP error, 5 link outside the allowed scope.
        """;

    /// <param name="args">Command-line arguments.</param>
    /// <param name="env">Environment values: FEDERATION_BASE_URL, FEDERATION_ACCESS_TOKEN, SHARD_ACCESS_TOKEN.</param>
    /// <param name="stdout">Results.</param>
    /// <param name="stderr">Errors.</param>
    /// <param name="federationHandler">Transport for the federation (tests supply a scripted one).</param>
    /// <param name="shardHandlerFactory">Transport per direct-read shard (tests supply scripted ones).</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, string?> env,
        TextWriter stdout,
        TextWriter stderr,
        HttpMessageHandler? federationHandler = null,
        Func<Uri, HttpMessageHandler>? shardHandlerFactory = null)
    {
        if (args.Any(arg => arg is "--help" or "-h"))
        {
            await stdout.WriteLineAsync(Usage);
            return 0;
        }

        if (!CliOptions.TryParse(args, env, out var options, out var error))
        {
            return await UsageErrorAsync(stderr, error);
        }

        try
        {
            return await ExecuteAsync(options, stdout, stderr, federationHandler, shardHandlerFactory);
        }
        catch (ArgumentException ex)
        {
            // The client validates URLs and the resource type; its messages never contain a credential.
            return await UsageErrorAsync(stderr, ex.Message);
        }
        catch (SearchExpiredException)
        {
            await stderr.WriteLineAsync(
                "error: the search continuation expired (HTTP 410 Gone). Restart the search from the original query; "
                + "nothing was replayed.");
            return 3;
        }
        catch (FederationRequestException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            foreach (var issue in ex.Issues)
            {
                await stderr.WriteLineAsync($"  issue {Describe(issue)}");
            }

            return 4;
        }
        catch (LinkRejectedException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return 5;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // Not ex.Message: a JSON error message can quote a position in the response.
            await stderr.WriteLineAsync("error: the response was not a valid FHIR response.");
            return 4;
        }
        catch (HttpRequestException ex)
        {
            await stderr.WriteLineAsync($"error: the request could not be completed ({ex.HttpRequestError}).");
            return 4;
        }
    }

    private static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        HttpMessageHandler? federationHandler,
        Func<Uri, HttpMessageHandler>? shardHandlerFactory)
    {
        using var client = new FederationClient(
            new FederationClientOptions
            {
                FederationBaseUrl = options.BaseUrl,
                FederationAccessToken = TokenSource(options.FederationToken),
                DirectReadShards =
                [
                    .. options.DirectShards.Select(shard => new ShardReadOptions
                    {
                        BaseUrl = shard,
                        AccessToken = TokenSource(options.ShardToken),
                    }),
                ],
            },
            federationHandler,
            shardHandlerFactory);

        var results = new SearchResultSet();
        var pageNumber = 0;
        await foreach (var page in client.SearchAsync(options.ResourceType, options.Parameters))
        {
            pageNumber++;
            results.Add(page);
            await PrintPageAsync(stdout, pageNumber, page);

            if (page.HasNext && pageNumber == options.MaxPages)
            {
                await stdout.WriteLineAsync($"stopped after {pageNumber} page(s): more pages are available (not fetched).");
                break;
            }
        }

        await stdout.WriteLineAsync(
            $"summary: pages={pageNumber} matches={results.Matches.Count} includes={results.Includes.Count} outcomes={results.Outcomes.Count}");

        return options.Read == ReadMode.None
            ? 0
            : await ReadFirstMatchAsync(client, results, options.Read, stdout, stderr);
    }

    private static async Task PrintPageAsync(TextWriter stdout, int number, SearchPage page)
    {
        await stdout.WriteLineAsync(
            $"page {number}: matches={page.Matches.Count} includes={page.Includes.Count} outcomes={page.Outcomes.Count}");

        foreach (var entry in page.Matches.Concat(page.Includes))
        {
            var shard = entry.SourceShard is null ? "" : $"  (shard={Clean(entry.SourceShard)})";
            await stdout.WriteLineAsync($"{entry.Mode.ToString().ToLowerInvariant()}  {Plain(entry.FullUrl)}{shard}");
        }

        foreach (var outcome in page.Outcomes)
        {
            await stdout.WriteLineAsync($"outcome {Describe(outcome)}");
        }
    }

    private static async Task<int> ReadFirstMatchAsync(
        FederationClient client, SearchResultSet results, ReadMode mode, TextWriter stdout, TextWriter stderr)
    {
        if (results.Matches.Count == 0)
        {
            await stdout.WriteLineAsync("read: there is no match to read.");
            return 0;
        }

        var entry = results.Matches[0];
        if (mode == ReadMode.Alternate && entry.Alternate is null)
        {
            await stderr.WriteLineAsync(
                "error: the first match has no alternate link, so it cannot be read through the federation. No request was sent.");
            return 5;
        }

        using var resource = mode == ReadMode.Alternate
            ? await client.ReadViaFederationAsync(entry)
            : await client.ReadDirectAsync(entry);

        var source = mode == ReadMode.Alternate ? entry.Alternate! : entry.FullUrl;
        var type = resource.RootElement.Str("resourceType");
        var id = resource.RootElement.Str("id");
        if (type is null || id is null)
        {
            throw new InvalidDataException("The read response is not a resource.");
        }

        await stdout.WriteLineAsync($"{Clean(type)}/{Clean(id)} read via {mode.ToString().ToLowerInvariant()} from {Plain(source)}");
        return 0;
    }

    private static async Task<int> UsageErrorAsync(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync($"error: {message}");
        await stderr.WriteLineAsync("Run with --help for usage.");
        return 2;
    }

    private static string Describe(OperationIssue issue) =>
        $"{Clean(issue.Severity)}/{Clean(issue.Code)}"
        + (issue.Diagnostics is null ? "" : $": {Clean(issue.Diagnostics)}")
        + (issue.SourceShard is null ? "" : $" (shard={Clean(issue.SourceShard)})");

    /// <summary>A URL as scheme, host, port and path only: no user info, query or fragment.</summary>
    private static string Plain(Uri url) =>
        $"{url.Scheme}://{url.IdnHost}{(url.IsDefaultPort ? "" : $":{url.Port}")}{url.AbsolutePath}";

    /// <summary>Server-supplied text is shown on one line; control characters could otherwise forge output lines.</summary>
    private static string Clean(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });

    private static Func<CancellationToken, ValueTask<string?>>? TokenSource(string? token) =>
        token is null ? null : _ => ValueTask.FromResult<string?>(token);
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ignixa.Federation.Tests.Integration.Search;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Ignixa.Federation.Tests.Integration.Reads;

/// <summary>A buffered read response with its exact bytes and every header the host set.</summary>
internal sealed class ReadResponse
{
    private ReadResponse(int status, IReadOnlyDictionary<string, string> headers, byte[] bytes)
    {
        Status = status;
        Headers = headers;
        Bytes = bytes;
    }

    public int Status { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public byte[] Bytes { get; }

    public string Body => Encoding.UTF8.GetString(Bytes);

    public string? ContentLocation => Headers.GetValueOrDefault("Content-Location");

    public string? ContentType => Headers.GetValueOrDefault("Content-Type");

    public string? IssueCode =>
        JsonElement.Parse(Body) is { } json && json.GetProperty("resourceType").GetString() == "OperationOutcome"
            ? json.GetProperty("issue")[0].GetProperty("code").GetString()
            : null;

    /// <summary>Gets whether the response is a complete failure: an OperationOutcome with no resource, location or validator headers.</summary>
    public bool IsCleanFailure =>
        Bytes.Length > 0 && IssueCode is not null && ContentLocation is null && !Headers.ContainsKey("ETag") && !Headers.ContainsKey("Last-Modified");

    /// <summary>Sends a GET through the in-memory server's client.</summary>
    public static async Task<ReadResponse> GetAsync(SearchHost host, string pathAndQuery, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IEnumerable<string> values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[name] = string.Join(", ", values);
        }

        return new ReadResponse((int)response.StatusCode, headers, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Sends a GET whose path reaches the application exactly as given. The test client resolves dot segments and
    /// decodes before sending, so it cannot carry these paths.
    /// </summary>
    public static async Task<ReadResponse> GetRawAsync(SearchHost host, string rawPath, string? token, CancellationToken aborted = default)
    {
        HttpContext http = await host.Server.SendAsync(
            context =>
            {
                context.Request.Method = HttpMethods.Get;
                context.Request.Path = rawPath;
                if (token is not null)
                {
                    context.Request.Headers[HeaderNames.Authorization] = "Bearer " + token;
                }

                if (aborted.CanBeCanceled)
                {
                    context.RequestAborted = aborted;
                }
            },
            TestContext.Current.CancellationToken);
        using var body = new MemoryStream();
        await http.Response.Body.CopyToAsync(body, TestContext.Current.CancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, Microsoft.Extensions.Primitives.StringValues values) in http.Response.Headers)
        {
            headers[name] = values.ToString();
        }

        return new ReadResponse(http.Response.StatusCode, headers, body.ToArray());
    }
}

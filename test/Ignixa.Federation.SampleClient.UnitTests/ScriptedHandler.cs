using System.Net;
using System.Text;

namespace Ignixa.Federation.SampleClient.UnitTests;

/// <summary>What a test handler saw on the wire for one request.</summary>
internal sealed record RecordedRequest(Uri Uri, string? Authorization, string? Accept);

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/>: replies are served in the order they were queued, and every request
/// (URI, Authorization and Accept headers) is recorded. No sockets are involved.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public ScriptedHandler Respond(HttpStatusCode status, string body, string? location = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
        };
        if (location is not null)
        {
            response.Headers.Location = new Uri(location);
        }

        _responses.Enqueue(() => response);
        return this;
    }

    public ScriptedHandler Ok(string body) => Respond(HttpStatusCode.OK, body);

    /// <summary>The next request fails at the transport level (timeout, truncated body, ...) instead of getting a response.</summary>
    public ScriptedHandler Throw(Exception failure)
    {
        _responses.Enqueue(() => throw failure);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedRequest(
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString()));

        return _responses.TryDequeue(out var response)
            ? Task.FromResult(response())
            : throw new InvalidOperationException($"No scripted response left for request #{Requests.Count}.");
    }
}

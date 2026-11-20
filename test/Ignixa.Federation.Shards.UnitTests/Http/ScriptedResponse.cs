using System.Text;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>One scripted answer from <see cref="LoopbackFhirServer"/>, written as raw HTTP/1.1 with <c>Connection: close</c> unless kept alive.</summary>
internal sealed class ScriptedResponse
{
    private ScriptedResponse(int status, byte[] body, string? contentType)
    {
        StatusCode = status;
        Body = body;
        ContentType = contentType;
    }

    public int StatusCode { get; }

    public byte[] Body { get; }

    public string? ContentType { get; }

    /// <summary>Gets whether the head declares <c>Content-Length</c>; without it the body ends when the connection closes.</summary>
    public bool DeclaresLength { get; private init; } = true;

    public string? Location { get; private init; }

    /// <summary>Runs after the request is recorded but before the server sends any response bytes.</summary>
    public Func<CancellationToken, Task>? BeforeHead { get; private init; }

    /// <summary>Gets whether the server never answers, so only the client's deadline or cancellation ends the call.</summary>
    public bool StallsBeforeHead { get; private init; }

    /// <summary>Gets whether the server sends the head and the body bytes, then stalls without closing.</summary>
    public bool StallsAfterBody { get; private init; }

    /// <summary>Gets whether the server keeps the connection open for another request after this response.</summary>
    public bool KeepsAlive { get; private init; }

    public static ScriptedResponse Json(string json, Func<CancellationToken, Task>? beforeHead = null) =>
        new(200, Encoding.UTF8.GetBytes(json), "application/fhir+json") { BeforeHead = beforeHead };

    public static ScriptedResponse Status(int status, string body = "") => new(status, Encoding.UTF8.GetBytes(body), "text/plain");

    public static ScriptedResponse Redirect(string location) => new(302, [], null) { Location = location };

    public static ScriptedResponse Stall() => new(200, [], null) { StallsBeforeHead = true };

    public ScriptedResponse WithoutContentLength() => new(StatusCode, Body, ContentType) { DeclaresLength = false, Location = Location };

    /// <summary>Declares a length one byte longer than the body sent, then stalls, so the client waits inside the body read.</summary>
    public ScriptedResponse StallingInBody() => new(StatusCode, Body, ContentType) { StallsAfterBody = true };

    /// <summary>Keeps the connection open after this response, so a client that drains the body can reuse it.</summary>
    public ScriptedResponse KeepingAlive() => new(StatusCode, Body, ContentType) { DeclaresLength = DeclaresLength, Location = Location, KeepsAlive = true };

    public byte[] Head()
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(StatusCode).Append(" Scripted\r\n");
        if (ContentType is not null)
        {
            head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
        }

        if (Location is not null)
        {
            head.Append("Location: ").Append(Location).Append("\r\n");
        }

        if (DeclaresLength)
        {
            head.Append("Content-Length: ").Append(Body.Length + (StallsAfterBody ? 1 : 0)).Append("\r\n");
        }

        head.Append(KeepsAlive ? "\r\n" : "Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(head.ToString());
    }
}

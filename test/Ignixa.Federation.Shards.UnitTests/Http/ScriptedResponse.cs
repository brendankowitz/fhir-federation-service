using System.Text;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>One scripted answer from <see cref="LoopbackFhirServer"/>, written as raw HTTP/1.1 with <c>Connection: close</c>.</summary>
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

    /// <summary>Gets whether the server never answers, so only the client's deadline or cancellation ends the call.</summary>
    public bool StallsBeforeHead { get; private init; }

    /// <summary>Gets whether the server sends the head and the body bytes, then stalls without closing.</summary>
    public bool StallsAfterBody { get; private init; }

    public static ScriptedResponse Json(string json) => new(200, Encoding.UTF8.GetBytes(json), "application/fhir+json");

    public static ScriptedResponse Status(int status, string body = "") => new(status, Encoding.UTF8.GetBytes(body), "text/plain");

    public static ScriptedResponse Redirect(string location) => new(302, [], null) { Location = location };

    public static ScriptedResponse Stall() => new(200, [], null) { StallsBeforeHead = true };

    public ScriptedResponse WithoutContentLength() => new(StatusCode, Body, ContentType) { DeclaresLength = false, Location = Location };

    /// <summary>Declares a length one byte longer than the body sent, then stalls, so the client waits inside the body read.</summary>
    public ScriptedResponse StallingInBody() => new(StatusCode, Body, ContentType) { StallsAfterBody = true };

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

        head.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(head.ToString());
    }
}

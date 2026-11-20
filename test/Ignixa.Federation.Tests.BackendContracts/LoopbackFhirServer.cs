using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ignixa.Federation.Tests.BackendContracts;

/// <summary>The bytes one HTTP/1.1 request put on the wire, decoded as Latin-1 so no escape is rewritten.</summary>
internal sealed record CapturedRequest(string RequestLine, string Headers, string Body);

/// <summary>
/// A single-connection loopback HTTP server for offline controls. It records the request exactly as the
/// real client sent it, so a test can see whether a percent escape survived URI construction, and answers
/// with a canned body. It listens on the loopback interface only and contacts nothing.
/// </summary>
internal sealed class LoopbackFhirServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackFhirServer()
    {
        _listener.Start();
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    /// <summary>Accepts one connection, records its request, replies with <paramref name="status"/> and <paramref name="responseBody"/>, and closes.</summary>
    public async Task<CapturedRequest> ServeOnceAsync(string responseBody, CancellationToken cancellationToken, int status = 200)
    {
        using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
        NetworkStream stream = client.GetStream();

        var received = new List<byte>();
        var buffer = new byte[1024];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            received.AddRange(buffer.AsSpan(0, await ReadSomeAsync(stream, buffer, cancellationToken)).ToArray());
        }

        string headerBlock = Encoding.Latin1.GetString([.. received.Take(headerEnd)]);
        string[] lines = headerBlock.Split("\r\n");
        int contentLength = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();

        int bodyStart = headerEnd + 4;
        while (received.Count - bodyStart < contentLength)
        {
            received.AddRange(buffer.AsSpan(0, await ReadSomeAsync(stream, buffer, cancellationToken)).ToArray());
        }

        string body = Encoding.Latin1.GetString([.. received.Skip(bodyStart).Take(contentLength)]);

        byte[] payload = Encoding.UTF8.GetBytes(responseBody);
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} Canned\r\nContent-Type: application/fhir+json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        return new CapturedRequest(lines[0], string.Join("\r\n", lines.Skip(1)), body);
    }

    public void Dispose() => _listener.Dispose();

    private static async Task<int> ReadSomeAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = await stream.ReadAsync(buffer, cancellationToken);
        return read > 0 ? read : throw new IOException("The client closed the connection before it finished the request.");
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (int i = 0; i + 3 < bytes.Count; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}

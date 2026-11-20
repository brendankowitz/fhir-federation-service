using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A TLS HTTP/1.1 server on <c>127.0.0.1</c> at an OS-assigned port. It records each request exactly as received and
/// answers from a script, one response per connection, so tests observe the client's real wire behaviour.
/// </summary>
internal sealed class LoopbackFhirServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<ScriptedResponse> _script = new();
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;

    public LoopbackFhirServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public string Origin => $"https://127.0.0.1:{Port}";

    public string BaseUrl => Origin + "/fhir";

    /// <summary>Gets the requests received so far, in arrival order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    /// <summary>Queues responses, answered in order; a request with nothing queued gets a 599.</summary>
    public void Respond(params ScriptedResponse[] responses)
    {
        foreach (ScriptedResponse response in responses)
        {
            _script.Enqueue(response);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Dispose();
        await _acceptLoop;
        await Task.WhenAll(_connections);
        _stopping.Dispose();
    }

    private static int IndexOfHeadEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i + 1;
            }
        }

        return -1;
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _connections.Add(ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = LoopbackCertificate.Server },
                    _stopping.Token);

                RecordedRequest? request = await ReadRequestAsync(tls);
                if (request is null)
                {
                    return;
                }

                _requests.Enqueue(request);
                ScriptedResponse response = _script.TryDequeue(out ScriptedResponse? scripted)
                    ? scripted
                    : ScriptedResponse.Status(599, "unscripted request");
                await WriteAsync(tls, response);
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up, refused the certificate, or the server is stopping; the test asserts on what it saw.
            }
        }
    }

    private async Task<RecordedRequest?> ReadRequestAsync(SslStream tls)
    {
        byte[] buffer = new byte[MaxHeadBytes];
        int length = 0;
        int headEnd;
        while ((headEnd = IndexOfHeadEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
            {
                return null;
            }

            int read = await tls.ReadAsync(buffer.AsMemory(length), _stopping.Token);
            if (read == 0)
            {
                return null;
            }

            length += read;
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, headEnd - 4).Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        var headers = new List<KeyValuePair<string, string>>();
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers.Add(new(line[..colon], line[(colon + 1)..].Trim()));
        }

        int bodyLength = headers
            .Where(h => string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(h => int.Parse(h.Value, System.Globalization.CultureInfo.InvariantCulture))
            .SingleOrDefault();
        byte[] body = new byte[bodyLength];
        int already = Math.Min(length - headEnd, bodyLength);
        Array.Copy(buffer, headEnd, body, 0, already);
        await tls.ReadExactlyAsync(body.AsMemory(already), _stopping.Token);

        return new RecordedRequest(requestLine[0], requestLine[1], headers, body);
    }

    private async Task WriteAsync(SslStream tls, ScriptedResponse response)
    {
        if (response.StallsBeforeHead)
        {
            await Task.Delay(Timeout.Infinite, _stopping.Token);
        }

        await tls.WriteAsync(response.Head(), _stopping.Token);
        await tls.WriteAsync(response.Body, _stopping.Token);
        await tls.FlushAsync(_stopping.Token);
        if (response.StallsAfterBody)
        {
            await Task.Delay(Timeout.Infinite, _stopping.Token);
        }

        await tls.ShutdownAsync();
    }
}

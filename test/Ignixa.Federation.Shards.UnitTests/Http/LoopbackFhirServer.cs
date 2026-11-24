using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A TLS HTTP/1.1 server on <c>127.0.0.1</c> at an OS-assigned port. It records each request exactly as received and
/// answers from a script, one response per connection unless a response keeps the connection alive, so tests observe the
/// client's real wire behaviour, including whether it reuses a connection.
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
    private int _connectionCount;

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

    /// <summary>
    /// Gets or sets an answer chosen from the request itself, for tests where concurrent callers (a refresher and a probe)
    /// make queue position unpredictable. A <see langword="null"/> answer falls back to the queue. Set before traffic.
    /// </summary>
    public Func<RecordedRequest, ScriptedResponse?>? Router { get; set; }

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
        using (_stopping)
        {
            try
            {
                // A just-accepted connection can send the loop around again during cancellation.
                using (_listener)
                {
                    await Task.WhenAll(_stopping.CancelAsync(), _acceptLoop);
                }
            }
            finally
            {
                await Task.WhenAll(_connections);
            }
        }
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
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException e) when (_stopping.IsCancellationRequested && e.SocketErrorCode == SocketError.OperationAborted)
            {
                // Windows can report an aborted accept as a socket error during cancellation.
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

                RecordedRequest? request;
                int connection = Interlocked.Increment(ref _connectionCount);
                while ((request = await ReadRequestAsync(tls, connection)) is not null)
                {
                    _requests.Enqueue(request);
                    ScriptedResponse response = Router?.Invoke(request)
                        ?? (_script.TryDequeue(out ScriptedResponse? scripted)
                            ? scripted
                            : ScriptedResponse.Status(599, "unscripted request"));
                    await WriteAsync(tls, response);
                    if (!response.KeepsAlive)
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up, refused the certificate, or the server is stopping; the test asserts on what it saw.
            }
        }
    }

    private async Task<RecordedRequest?> ReadRequestAsync(SslStream tls, int connection)
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

        return new RecordedRequest(requestLine[0], requestLine[1], headers, body, connection);
    }

    private async Task WriteAsync(SslStream tls, ScriptedResponse response)
    {
        if (response.BeforeHead is { } beforeHead)
        {
            await beforeHead(_stopping.Token);
        }

        if (response.HangsUp)
        {
            return;
        }

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

        if (!response.KeepsAlive)
        {
            await tls.ShutdownAsync();
        }
    }
}

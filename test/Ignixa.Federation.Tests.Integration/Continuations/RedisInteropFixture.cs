using System.Globalization;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Opt-in connection to a real, explicitly launched, disposable Redis process for
/// <see cref="RedisContinuationCacheInteropTests"/>. The opt-in is the pair <see cref="PortVariable"/> and
/// <see cref="PasswordVariable"/>, supplied to the test process only by the launcher of that Redis process. With
/// neither variable, the interop tests are skipped (not passed). With one, or with values that fail validation or
/// identity checks, they fail closed. Every connection, including inspection and reconnects, dials only the numerical
/// 127.0.0.1 endpoint on the given port, whatever endpoints the client learns; it never resolves a host name, and never
/// reports the port, the password, or a client exception (which can name the endpoint).
/// </summary>
public sealed class RedisInteropFixture : IAsyncLifetime
{
    internal const string PortVariable = "FEDERATION_TEST_REDIS_PORT";
    internal const string PasswordVariable = "FEDERATION_TEST_REDIS_PASSWORD";

    // The exact-expiry tests need TIME to stay frozen across the staging prefix and the adapter script.
    private static readonly Version MinimumVersion = new(7, 2);

    private ConnectionMultiplexer? _first;
    private ConnectionMultiplexer? _second;
    private ConnectionMultiplexer? _resp3;
    private ConnectionMultiplexer? _inspector;
    private RedisInteropServer? _server;
    private string? _refusal;
    private string? _version;

    public async ValueTask InitializeAsync()
    {
        string? port = Environment.GetEnvironmentVariable(PortVariable);
        string? password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (port is null && password is null)
        {
            return;
        }

        if (port is null || password is null)
        {
            _refusal = $"Real Redis interop needs both {PortVariable} and {PasswordVariable}; only one was supplied.";
            return;
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int portNumber) || portNumber is < 1 or > 65535)
        {
            _refusal = $"{PortVariable} is not a TCP port number.";
            return;
        }

        if (password.Length == 0)
        {
            _refusal = $"{PasswordVariable} is empty.";
            return;
        }

        try
        {
            // INFO is an admin command to the client, so it has its own connection; the adapter's two are not admin.
            _inspector = await ConnectAsync(portNumber, password, allowAdmin: true, RedisProtocol.Resp2);
            _refusal = await VerifyAsync(_inspector.GetDatabase(), portNumber);
            if (_refusal is null)
            {
                _first = await ConnectAsync(portNumber, password, allowAdmin: false, RedisProtocol.Resp2);
                _second = await ConnectAsync(portNumber, password, allowAdmin: false, RedisProtocol.Resp2);
                _resp3 = await ConnectAsync(portNumber, password, allowAdmin: false, RedisProtocol.Resp3);
                if (_first.GetServers().Any(s => s.Protocol != RedisProtocol.Resp2) || _resp3.GetServers().Any(s => s.Protocol != RedisProtocol.Resp3))
                {
                    _refusal = "The interop connections did not negotiate the requested RESP2 and RESP3 protocols.";
                    return;
                }

                _server = new RedisInteropServer(
                    _first.GetDatabase(), _second.GetDatabase(), _resp3.GetDatabase(), _inspector.GetDatabase(), _version!);
            }
        }
        catch (Exception exception) when (exception is RedisException or RedisTimeoutException or RedisCommandException)
        {
            // The client's message can carry the endpoint, so none of it is surfaced.
            _refusal = "Could not connect to, authenticate with, or inspect the loopback Redis process.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ConnectionMultiplexer? connection in new[] { _first, _second, _resp3, _inspector })
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>Gets the verified server, skips the calling test when interop was not requested, or fails it closed.</summary>
    public RedisInteropServer Require()
    {
        if (_server is not null)
        {
            return _server;
        }

        if (_refusal is not null)
        {
            throw new InvalidOperationException(_refusal);
        }

        Assert.Skip($"Real Redis interop not requested: {PortVariable} and {PasswordVariable} are not set. "
            + "No real Redis behaviour was validated by this test.");
        return null!;
    }

    private static Task<ConnectionMultiplexer> ConnectAsync(int port, string password, bool allowAdmin, RedisProtocol protocol) =>
        ConnectionMultiplexer.ConnectAsync(ConnectionOptions(port, password, allowAdmin, protocol));

    internal static ConfigurationOptions ConnectionOptions(int port, string password, bool allowAdmin, RedisProtocol protocol) =>
        new()
        {
            EndPoints = { new IPEndPoint(IPAddress.Loopback, port) },
            Password = password,
            Protocol = protocol,
            AbortOnConnectFail = true,
            ConnectRetry = 1,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000,
            AllowAdmin = allowAdmin,
            TieBreaker = string.Empty,
            ConfigurationChannel = string.Empty,
            ClientName = "ignixa-federation-interop-tests",
            Tunnel = new LoopbackEndpointGuard(port),
        };

    // Every physical connection the client opens (initial, learned from topology or redirects, interactive or
    // subscription, and every reconnect) first asks the tunnel which endpoint to dial, before creating a socket. Only
    // the exact numerical 127.0.0.1 endpoint and port is dialed; for anything else no socket is created, and the
    // connection fails at the authentication hook, which the client records as a connection failure.
    private sealed class LoopbackEndpointGuard(int port) : Tunnel
    {
        public override ValueTask<EndPoint?> GetSocketConnectEndpointAsync(EndPoint endpoint, CancellationToken cancellationToken) =>
            new(endpoint is IPEndPoint { AddressFamily: AddressFamily.InterNetwork } ip && ip.Address.Equals(IPAddress.Loopback) && ip.Port == port
                ? new IPEndPoint(IPAddress.Loopback, port)
                : null);

        public override ValueTask<Stream?> BeforeAuthenticateAsync(
            EndPoint endpoint, ConnectionType connectionType, Socket? socket, CancellationToken cancellationToken) =>
            socket is null
                ? throw new InvalidOperationException("Refused a Redis endpoint other than the interop fixture's loopback endpoint.")
                : default;
    }

    // The identity checks: a standalone primary at least MinimumVersion, listening on the port it was given, with an
    // empty key space (a fresh disposable process, not a store with someone's data).
    private async Task<string?> VerifyAsync(IDatabase database, int port)
    {
        Dictionary<string, string> server = Info(await database.ExecuteAsync("INFO", "server"));
        Dictionary<string, string> replication = Info(await database.ExecuteAsync("INFO", "replication"));
        Dictionary<string, string> keyspace = Info(await database.ExecuteAsync("INFO", "keyspace"));

        if (!server.TryGetValue("redis_version", out string? version) || !Version.TryParse(version, out Version? parsed) || parsed < MinimumVersion)
        {
            return $"The interop process is not Redis {MinimumVersion} or later.";
        }

        if (!server.TryGetValue("redis_mode", out string? mode) || mode != "standalone")
        {
            return "The interop process is not a standalone Redis.";
        }

        if (!server.TryGetValue("tcp_port", out string? listening) || listening != port.ToString(CultureInfo.InvariantCulture))
        {
            return "The interop process does not report the port it was reached on.";
        }

        if (!replication.TryGetValue("role", out string? role) || role != "master")
        {
            return "The interop process is not a primary.";
        }

        if (keyspace.Keys.Any(k => k.StartsWith("db", StringComparison.Ordinal)))
        {
            return "The interop process already holds keys; it is not a fresh disposable instance.";
        }

        _version = version;
        return null;
    }

    private static Dictionary<string, string> Info(RedisResult reply)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in ((string?)reply ?? string.Empty).Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && !trimmed.StartsWith('#', StringComparison.Ordinal))
            {
                fields[trimmed[..colon]] = trimmed[(colon + 1)..];
            }
        }

        return fields;
    }
}

/// <summary>
/// A verified interop server: two independent, non-admin RESP2 connections and a non-admin RESP3 connection for
/// adapters, and an admin connection used only to inspect the server (<c>INFO</c>).
/// </summary>
public sealed record RedisInteropServer(IDatabase First, IDatabase Second, IDatabase Resp3, IDatabase Inspector, string Version);

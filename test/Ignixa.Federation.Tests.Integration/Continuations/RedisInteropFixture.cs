using System.Globalization;
using System.Net;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Opt-in connection to a real, explicitly launched, disposable Redis process for
/// <see cref="RedisContinuationCacheInteropTests"/>. The opt-in is the pair <see cref="PortVariable"/> and
/// <see cref="PasswordVariable"/>, supplied to the test process only by the launcher of that Redis process. With
/// neither variable, the interop tests are skipped (not passed). With one, or with values that fail validation or
/// identity checks, they fail closed. It connects to 127.0.0.1 only, never resolves a host name, and never reports the
/// port, the password, or a client exception (which can name the endpoint).
/// </summary>
public sealed class RedisInteropFixture : IAsyncLifetime
{
    internal const string PortVariable = "FEDERATION_TEST_REDIS_PORT";
    internal const string PasswordVariable = "FEDERATION_TEST_REDIS_PASSWORD";

    private static readonly Version MinimumVersion = new(7, 0);

    private ConnectionMultiplexer? _first;
    private ConnectionMultiplexer? _second;
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
            _inspector = await ConnectAsync(portNumber, password, allowAdmin: true);
            _refusal = await VerifyAsync(_inspector.GetDatabase(), portNumber);
            if (_refusal is null)
            {
                _first = await ConnectAsync(portNumber, password, allowAdmin: false);
                _second = await ConnectAsync(portNumber, password, allowAdmin: false);
                _server = new RedisInteropServer(_first.GetDatabase(), _second.GetDatabase(), _inspector.GetDatabase(), _version!);
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
        foreach (ConnectionMultiplexer? connection in new[] { _first, _second, _inspector })
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

    private static async Task<ConnectionMultiplexer> ConnectAsync(int port, string password, bool allowAdmin)
    {
        var options = new ConfigurationOptions
        {
            EndPoints = { new IPEndPoint(IPAddress.Loopback, port) },
            Password = password,
            AbortOnConnectFail = true,
            ConnectRetry = 1,
            ConnectTimeout = 5000,
            SyncTimeout = 5000,
            AsyncTimeout = 5000,
            AllowAdmin = allowAdmin,
            TieBreaker = string.Empty,
            ConfigurationChannel = string.Empty,
            ClientName = "ignixa-federation-interop-tests",
        };
        return await ConnectionMultiplexer.ConnectAsync(options);
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
/// A verified interop server: two independent, non-admin connections for adapters, and an admin connection used only
/// to inspect the server (<c>INFO</c>).
/// </summary>
public sealed record RedisInteropServer(IDatabase First, IDatabase Second, IDatabase Inspector, string Version);

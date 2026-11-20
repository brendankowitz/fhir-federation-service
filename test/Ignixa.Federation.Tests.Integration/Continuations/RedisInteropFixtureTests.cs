using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Shouldly;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// The containment of <see cref="RedisInteropFixture"/>'s connections: whatever endpoints a multiplexer built from the
/// fixture's options learns, from its seed, from server topology, or on reconnect, it dials only the one numerical
/// IPv4 <c>127.0.0.1</c> endpoint and port the fixture was given. No process and no external target: the checks use
/// the options' own dial hook offline, and listeners this test owns on loopback.
/// </summary>
public sealed class RedisInteropFixtureTests
{
    private const int AllowedPort = 6390;
    private const string Password = "not-a-secret";

    [Fact]
    public async Task FixtureOptions_ResolveOnlyTheExactLoopbackEndpointForDialing()
    {
        foreach (RedisProtocol protocol in new[] { RedisProtocol.Resp2, RedisProtocol.Resp3 })
        {
            foreach (bool allowAdmin in new[] { true, false })
            {
                ConfigurationOptions options = RedisInteropFixture.ConnectionOptions(AllowedPort, Password, allowAdmin, protocol);
                options.Tunnel.ShouldNotBeNull("Fixture connections have no dial guard.");

                EndPoint? dialed = await options.Tunnel.GetSocketConnectEndpointAsync(new IPEndPoint(IPAddress.Loopback, AllowedPort), TestContext.Current.CancellationToken);
                dialed.ShouldBeOfType<IPEndPoint>().ShouldSatisfyAllConditions(
                    ip => ip.AddressFamily.ShouldBe(AddressFamily.InterNetwork),
                    ip => ip.Address.ShouldBe(IPAddress.Loopback),
                    ip => ip.Port.ShouldBe(AllowedPort));

                foreach ((string label, EndPoint endpoint) in Disallowed(AllowedPort, AllowedPort + 1))
                {
                    (await options.Tunnel.GetSocketConnectEndpointAsync(endpoint, TestContext.Current.CancellationToken))
                        .ShouldBeNull($"The fixture would dial {label}.");
                }
            }
        }
    }

    [Fact]
    public async Task FixtureOptions_FailARefusedEndpointBeforeAuthenticationAndPassTheAllowedSocketThrough()
    {
        ConfigurationOptions options = RedisInteropFixture.ConnectionOptions(AllowedPort, Password, allowAdmin: true, RedisProtocol.Resp2);
        options.Tunnel.ShouldNotBeNull("Fixture connections have no dial guard.");
        var refused = new IPEndPoint(IPAddress.Loopback, AllowedPort + 1);

        // A refused endpoint reaches authentication with no socket; failing there is how the client records it.
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            async () => await options.Tunnel.BeforeAuthenticateAsync(refused, ConnectionType.Interactive, null, TestContext.Current.CancellationToken));
        failure.Message.ShouldNotContain((AllowedPort + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Never connected: only handed to the hook, which must leave the transport to the client.
        using var unconnected = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        (await options.Tunnel.BeforeAuthenticateAsync(
            new IPEndPoint(IPAddress.Loopback, AllowedPort), ConnectionType.Interactive, unconnected, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AMultiplexerFromFixtureOptions_DialsOnlyItsEndpoint_IncludingOnReconnect()
    {
        // The allowed listener stands in for Redis but never answers, so the client keeps reconnecting to it. The
        // decoys listen on another loopback port, over IPv4 and IPv6, where any dial to a refused alias would land.
        await using var allowed = CountingListener.Start(IPAddress.Loopback, 0)!;
        await using var decoy = CountingListener.Start(IPAddress.Loopback, 0)!;
        await using CountingListener? decoyV6 = CountingListener.Start(IPAddress.IPv6Loopback, decoy.Port);

        ConfigurationOptions options = RedisInteropFixture.ConnectionOptions(allowed.Port, Password, allowAdmin: true, RedisProtocol.Resp2);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 1000;

        // Extra seeds take the same connect path as endpoints learned from topology or redirects.
        foreach ((_, EndPoint endpoint) in Disallowed(allowed.Port, decoy.Port).Where(d => d.Endpoint is DnsEndPoint || ((IPEndPoint)d.Endpoint).Port == decoy.Port))
        {
            options.EndPoints.Add(endpoint);
        }

        {
            await using ConnectionMultiplexer multiplexer = await ConnectionMultiplexer.ConnectAsync(options);
            await WaitUntil(() => allowed.Accepted > 0, "The client never dialed the allowed endpoint.");

            int initial = allowed.Accepted;
            allowed.DropAll();
            await WaitUntil(() => allowed.Accepted > initial, "The client never reconnected to the allowed endpoint.");
        }

        decoy.Accepted.ShouldBe(0, "A fixture connection dialed another loopback port.");
        (decoyV6?.Accepted ?? 0).ShouldBe(0, "A fixture connection dialed an IPv6 loopback alias.");
    }

    // Endpoints the fixture must never dial, given the allowed port and another loopback port.
    private static IEnumerable<(string Label, EndPoint Endpoint)> Disallowed(int allowedPort, int otherPort) =>
    [
        ("another loopback port", new IPEndPoint(IPAddress.Loopback, otherPort)),
        ("another IPv4 loopback address", new IPEndPoint(IPAddress.Parse("127.0.0.2"), allowedPort)),
        ("the IPv4 any address", new IPEndPoint(IPAddress.Any, allowedPort)),
        ("a documentation-range address", new IPEndPoint(IPAddress.Parse("203.0.113.7"), allowedPort)),
        ("the IPv6 loopback", new IPEndPoint(IPAddress.IPv6Loopback, allowedPort)),
        ("the IPv6 loopback on another port", new IPEndPoint(IPAddress.IPv6Loopback, otherPort)),
        ("the IPv4-mapped loopback", new IPEndPoint(IPAddress.Loopback.MapToIPv6(), allowedPort)),
        ("the IPv4-mapped loopback on another port", new IPEndPoint(IPAddress.Loopback.MapToIPv6(), otherPort)),
        ("an unresolved localhost", new DnsEndPoint("localhost", allowedPort)),
        ("an unresolved localhost on another port", new DnsEndPoint("localhost", otherPort)),
        ("an unresolved numeric host", new DnsEndPoint("127.0.0.1", allowedPort)),
    ];

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        for (int attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue(failure);
    }

    // A loopback listener this test owns: it accepts and counts connections and never answers them.
    private sealed class CountingListener : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentQueue<TcpClient> _clients = new();
        private readonly Task _accepting;
        private int _accepted;

        private CountingListener(TcpListener listener)
        {
            _listener = listener;
            _accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        // Null when the address cannot be bound here, as IPv6 loopback can be on some hosts.
        public static CountingListener? Start(IPAddress address, int port)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                return new CountingListener(listener);
            }
            catch (SocketException)
            {
                listener.Dispose();
                return null;
            }
        }

        public void DropAll()
        {
            while (_clients.TryDequeue(out TcpClient? client))
            {
                client.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _accepting;
            DropAll();
            _listener.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref _accepted);
                    _clients.Enqueue(client);
                }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopped.
            }
        }
    }
}

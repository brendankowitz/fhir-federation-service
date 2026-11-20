using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Shouldly;
using StackExchange.Redis;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// The containment of <see cref="RedisInteropFixture"/>'s connections: whatever endpoint a multiplexer built from the
/// fixture's options attempts, it dials only the one numerical IPv4 <c>127.0.0.1</c> endpoint and port the fixture was
/// given. No process and no external target: every candidate alias is checked offline against the options' own dial
/// hook, and the live checks name only the exact endpoints of listeners this test has bound and owns.
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
    public async Task AMultiplexerFromFixtureOptions_NeverDialsAnIPv4LoopbackDecoy()
    {
        await using CountingListener decoy = CountingListener.Start(IPAddress.Loopback);
        await AssertDialsOnlyTheAllowedListener(decoy);
    }

    [Fact]
    public async Task AMultiplexerFromFixtureOptions_NeverDialsAnIPv6LoopbackDecoy()
    {
        await using CountingListener? decoy = CountingListener.TryStartOnIPv6Loopback();
        if (decoy is null)
        {
            Assert.Skip("IPv6 loopback cannot be bound on this host, so its live dial check did not run. "
                + "The offline check still refuses IPv6 and IPv4-mapped endpoints.");
        }

        await AssertDialsOnlyTheAllowedListener(decoy);
    }

    // The live options name exactly two numerical endpoints, the allowed listener and the decoy, both bound and owned by
    // this test until after the multiplexer is disposed. So even with the guard missing, no dial can reach anything the
    // test does not own: never a host name, an unbound address or port, or a remote address. Neither listener ever
    // answers, so no connection completes a handshake. Every repeated dial is the client's connect retry, not an
    // authenticated reconnect, and no topology is ever learned.
    private static async Task AssertDialsOnlyTheAllowedListener(CountingListener decoy)
    {
        await using CountingListener allowed = CountingListener.Start(IPAddress.Loopback);

        ConfigurationOptions options = RedisInteropFixture.ConnectionOptions(allowed.Port, Password, allowAdmin: true, RedisProtocol.Resp2);
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 1000;
        options.EndPoints.Add(decoy.EndPoint);
        options.EndPoints.ShouldBe([allowed.EndPoint, decoy.EndPoint], ignoreOrder: true);

        await using (ConnectionMultiplexer multiplexer = await ConnectionMultiplexer.ConnectAsync(options))
        {
            IServer allowedServer = multiplexer.GetServer(allowed.EndPoint);
            IServer decoyServer = multiplexer.GetServer(decoy.EndPoint);

            // The client counts a connection attempt before asking the tunnel what to dial; the listener counts dials.
            await WaitUntil(() => decoy.Accepted > 0 || Attempts(decoyServer) >= 2, "The client never attempted the decoy twice.");
            decoy.Accepted.ShouldBe(0, "A fixture connection dialed the decoy.");
            await WaitUntil(() => allowed.Accepted > 0, "The client never dialed the allowed endpoint.");

            // After the test closes the allowed listener's half-open connections, the client dials it again, through the
            // per-bridge connect path a reconnect also takes.
            int initial = allowed.Accepted;
            allowed.DropAll();
            await WaitUntil(() => allowed.Accepted > initial, "The client never dialed the allowed endpoint again.");
            allowedServer.IsConnected.ShouldBeFalse("A never-answering listener completed a handshake.");
        }

        decoy.Accepted.ShouldBe(0, "A fixture connection dialed the decoy.");
    }

    private static long Attempts(IServer server) => server.GetCounters().Interactive.SocketCount;
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

        // The numerical endpoint the listener is bound to.
        public IPEndPoint EndPoint => (IPEndPoint)_listener.LocalEndpoint;

        public int Port => EndPoint.Port;

        public int Accepted => Volatile.Read(ref _accepted);

        // On an ephemeral port of the given address, held exclusively on Windows so no other socket can share it.
        public static CountingListener Start(IPAddress address)
        {
            var listener = new TcpListener(address, 0);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    listener.ExclusiveAddressUse = true;
                }

                listener.Start();
            }
            catch
            {
                listener.Dispose();
                throw;
            }

            return new CountingListener(listener);
        }

        // Null only when the host has no IPv6 loopback to bind; any other bind failure fails the test.
        public static CountingListener? TryStartOnIPv6Loopback()
        {
            if (!Socket.OSSupportsIPv6)
            {
                return null;
            }

            try
            {
                return Start(IPAddress.IPv6Loopback);
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable)
            {
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

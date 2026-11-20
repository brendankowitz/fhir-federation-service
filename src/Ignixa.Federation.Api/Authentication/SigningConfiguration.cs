using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ignixa.Federation.Api.Authentication;

/// <summary>Owns signing discovery for one host. Request callers only read the published snapshot or queue a hint.</summary>
internal sealed partial class SigningConfiguration(
    Uri authority,
    string issuer,
    TimeProvider time,
    Func<HttpMessageHandler?> getBackchannelHandler,
    ILogger<SigningConfiguration> logger)
    : BackgroundService, IConfigurationManager<OpenIdConnectConfiguration>, IHealthCheck
{
    public const string HealthCheckName = "caller-signing";

    // Independent of the request budget: one 10 s attempt, scheduled every 5 min or at most once per 30 s
    // for retries and key-miss hints. An outage never extends the one-hour lifetime of a previous snapshot.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);
    private const int MaxDocumentBytes = 256 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Channel<bool> _hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true });
    private Snapshot? _snapshot;

    public long? FreshPublicationTimestamp => GetFreshSnapshot()?.PublishedAt;

    public bool IsFresh(long publishedAt) => time.GetElapsedTime(publishedAt) < MaxAge;

    /// <inheritdoc />
    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        // JwtBearer owns the mutable configuration it receives. The worker's immutable key list is never handed out.
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        if (GetFreshSnapshot() is { } snapshot)
        {
            foreach (SecurityKey key in snapshot.Keys)
            {
                configuration.SigningKeys.Add(key);
            }
        }

        return Task.FromResult(configuration);
    }

    /// <inheritdoc />
    public void RequestRefresh() => _hints.Writer.TryWrite(true);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetFreshSnapshot() is not null
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Caller signing configuration is not ready."));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only the hosted loop resolves the named options for its backchannel, after DI has constructed this service.
        string baseAddress = authority.AbsoluteUri;
        Uri metadata = new(baseAddress + (baseAddress[^1] == '/' ? "" : "/") + ".well-known/openid-configuration");
        using HttpMessageHandler handler = CreateBackchannelHandler(getBackchannelHandler());
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            _hints.Reader.TryRead(out _);
            long started = time.GetTimestamp();
            bool succeeded = await RefreshAsync(client, metadata, started, stoppingToken);
            await WaitForNextAttemptAsync(started, succeeded, stoppingToken);
        }
    }

    private Snapshot? GetFreshSnapshot() =>
        Volatile.Read(ref _snapshot) is { } snapshot && IsFresh(snapshot.PublishedAt) ? snapshot : null;

    private static HttpMessageHandler CreateBackchannelHandler(HttpMessageHandler? supplied) => supplied ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // Disposing a rejected or timed-out response must not start a background body drain.
        MaxResponseDrainSize = 0,
    };

    private async Task<bool> RefreshAsync(HttpClient client, Uri metadata, long started, CancellationToken stoppingToken)
    {
        using var timer = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, stoppingToken);
        CancellationToken cancel = linked.Token;
        try
        {
            string discovery = await ReadDocumentAsync(client, metadata, started, cancel);
            var configuration = new OpenIdConnectConfiguration(discovery);
            if (!string.Equals(configuration.Issuer, issuer, StringComparison.Ordinal)
                || !Uri.TryCreate(configuration.JwksUri, UriKind.Absolute, out Uri? jwks)
                || jwks.Scheme != Uri.UriSchemeHttps
                || jwks.UserInfo.Length != 0
                || jwks.Fragment.Length != 0)
            {
                throw new FormatException("Invalid caller signing discovery.");
            }

            string document = await ReadDocumentAsync(client, jwks, started, cancel);
            var keySet = new JsonWebKeySet(document);
            ImmutableArray<SecurityKey> keys = [.. keySet.GetSigningKeys().Where(CanVerifySignature)];
            if (keys.IsEmpty)
            {
                throw new FormatException("No usable caller signing keys.");
            }

            long completed = time.GetTimestamp();
            CheckFetchDeadline(started, completed, cancel);
            Volatile.Write(ref _snapshot, new Snapshot(completed, keys));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
            or ArgumentException or FormatException or JsonException or CryptographicException or TimeoutException)
        {
            stoppingToken.ThrowIfCancellationRequested();
            // Never log an exception from HTTP or parsing: its message can carry a URL, a body or key data.
            LogRefreshFailed(logger);
            return false;
        }
    }

    private static bool CanVerifySignature(SecurityKey key)
    {
        CryptoProviderFactory crypto = key.CryptoProviderFactory;
        if (crypto.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256, key))
        {
            if (key is not RsaSecurityKey { Rsa: null } rsa)
            {
                return key.KeySize >= 1024;
            }

            try
            {
                // IdentityModel can resolve an RSA JWK from byte arrays before the platform has imported its parameters.
                using RSA imported = RSA.Create(rsa.Parameters);
                return imported.KeySize >= 1024;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        return crypto.IsSupportedAlgorithm(SecurityAlgorithms.EcdsaSha256, key)
            || crypto.IsSupportedAlgorithm(SecurityAlgorithms.EcdsaSha384, key)
            || crypto.IsSupportedAlgorithm(SecurityAlgorithms.EcdsaSha512, key);
    }

    private void CheckFetchDeadline(long started, long now, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (time.GetElapsedTime(started, now) >= FetchTimeout)
        {
            throw new TimeoutException("Caller signing discovery exceeded its deadline.");
        }
    }

    private async Task<string> ReadDocumentAsync(HttpClient client, Uri address, long started, CancellationToken cancel)
    {
        CheckFetchDeadline(started, time.GetTimestamp(), cancel);
        using HttpResponseMessage response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
        CheckFetchDeadline(started, time.GetTimestamp(), cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDocumentBytes)
        {
            throw new IOException("Caller signing document exceeds its byte limit.");
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancel);
        // Read one extra byte to detect overflow even for responses without Content-Length.
        byte[] bytes = new byte[MaxDocumentBytes + 1];
        int length = await body.ReadAtLeastAsync(bytes, bytes.Length, false, cancel);
        CheckFetchDeadline(started, time.GetTimestamp(), cancel);
        if (length > MaxDocumentBytes)
        {
            throw new IOException("Caller signing document exceeds its byte limit.");
        }

        int offset = bytes.AsSpan(0, length).StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        return Utf8.GetString(bytes, offset, length - offset);
    }

    private async Task WaitForNextAttemptAsync(long started, bool succeeded, CancellationToken stoppingToken)
    {
        TimeSpan cooldownLeft = Cooldown - time.GetElapsedTime(started);
        if (cooldownLeft > TimeSpan.Zero)
        {
            await Task.Delay(cooldownLeft, time, stoppingToken);
        }

        TimeSpan refreshDelay = RefreshInterval - time.GetElapsedTime(started);
        if (succeeded && refreshDelay > TimeSpan.Zero)
        {
            using var timer = new CancellationTokenSource(refreshDelay, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timer.Token, stoppingToken);
            try
            {
                await _hints.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timer.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Regular refresh became due without a key-miss hint.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caller signing configuration refresh failed.")]
    private static partial void LogRefreshFailed(ILogger logger);

    private sealed record Snapshot(long PublishedAt, ImmutableArray<SecurityKey> Keys);
}

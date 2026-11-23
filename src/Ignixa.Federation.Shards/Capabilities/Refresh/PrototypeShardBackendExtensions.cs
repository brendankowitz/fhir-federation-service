using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ignixa.Federation.Shards.Capabilities.Refresh;

/// <summary>
/// Composes the prototype shard backend from bound <see cref="FederationOptions"/>: the prototype backend catalog, Azure
/// shard credentials, and the existing shared router and capability refresh.
/// </summary>
public static class PrototypeShardBackendExtensions
{
    /// <summary>
    /// Validates the federation settings, the per-shard wire ceilings and the secret mount inputs, and only then registers
    /// the shared shard backend through <see cref="ShardCapabilityRefreshExtensions.AddShardBackend(IServiceCollection, ShardRegistry, IShardCredentialProvider, TimeSpan, long, IReadOnlyDictionary{ShardId, ShardRequestLimits})"/>.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="options">The bound federation options. The shard registry, call deadline and response limit are captured at registration.</param>
    /// <param name="wireLimits">
    /// Exactly one search ceiling per configured shard, matched by <see cref="ShardId"/> equality whatever comparer the
    /// dictionary uses. The mapping is copied.
    /// </param>
    /// <param name="secretMountRoot">
    /// The fully qualified local directory of the operator-provisioned secret mount. Required, and only inspected, when a
    /// shard uses client credentials; managed-identity-only configurations never look at it.
    /// </param>
    /// <param name="secretFiles">
    /// The logical secret name to leaf file name map, compared ordinally. Required, and only inspected, when a shard uses
    /// client credentials. Every client-credentials <c>SecretName</c> must be a key.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>Nothing is registered unless every check passes. Registration reads no secret file, requests no token and sends
    /// nothing; a client-credentials secret is read only when a backend call needs it, and fails closed. The backend call
    /// deadline must be positive, no longer than the transport can enforce (<c>uint.MaxValue - 1</c> milliseconds) and no
    /// longer than <c>Timeouts.Request</c>; <c>Limits.MaxResponseBytes</c> must be positive and at most
    /// <see cref="Array.MaxLength"/>. Tenant and client identifiers are checked syntactically, without creating an SDK
    /// credential or verifying identity existence, permissions or token issuance.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/>, <paramref name="options"/> or <paramref name="wireLimits"/> is <see langword="null"/>.</exception>
    /// <exception cref="OptionsValidationException">
    /// Any input is invalid. Each failure starts with its configuration path (an index, never a submitted value) and no
    /// submitted value or secret is echoed. There is no inner exception.
    /// </exception>
    /// <exception cref="InvalidOperationException">A registry, router or refresher is already registered; nothing is changed.</exception>
    public static IServiceCollection AddPrototypeShardBackend(
        this IServiceCollection services,
        FederationOptions options,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> wireLimits,
        string? secretMountRoot = null,
        IEnumerable<KeyValuePair<string, string>>? secretFiles = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wireLimits);

        var failures = new List<string>();
        TimeSpan backendCall = options.Timeouts.BackendCall;
        long maxResponseBytes = options.Limits.MaxResponseBytes;
        CheckTransport(backendCall, options.Timeouts.Request, maxResponseBytes, failures);

        _ = ShardRegistry.TryCreate(options.Shards, PrototypeBackendCatalog.Profiles, out ShardRegistry? registry, out IReadOnlyList<string> registryErrors);
        failures.AddRange(registryErrors);

        Dictionary<ShardId, ShardRequestLimits> limits = CopyLimits(wireLimits, failures);
        MountedShardSecretResolver? mounted = null;
        if (registry is not null)
        {
            CheckLimitsCoverShards(registry, limits, failures);
            if (registry.Shards.Any(shard => shard.Auth is ClientCredentialsAuth))
            {
                mounted = BuildMount(secretMountRoot, secretFiles, failures);
            }

            foreach (ShardDescriptor shard in registry.Shards)
            {
                CheckAuth(shard, mounted, failures);
            }
        }

        if (registry is null || failures.Count > 0)
        {
            throw new OptionsValidationException(nameof(FederationOptions), typeof(FederationOptions), failures);
        }

        IShardSecretResolver secrets = mounted is null ? NoSecrets.Instance : mounted;
        return services.AddShardBackend(registry, new AzureShardCredentialProvider(secrets), backendCall, maxResponseBytes, limits);
    }

    private static void CheckTransport(TimeSpan backendCall, TimeSpan request, long maxResponseBytes, List<string> failures)
    {
        const string callPath = "Federation:Timeouts:BackendCall";
        if (backendCall <= TimeSpan.Zero || backendCall > HttpShardClient.MaxCallTimeout)
        {
            failures.Add($"{callPath}: must be positive and no longer than the transport deadline limit.");
        }

        if (backendCall > request)
        {
            failures.Add($"{callPath}: must not exceed Federation:Timeouts:Request.");
        }

        if (maxResponseBytes <= 0 || maxResponseBytes > Array.MaxLength)
        {
            failures.Add("Federation:Limits:MaxResponseBytes: must be positive and no larger than the largest supported array.");
        }
    }

    // Copied under default ShardId equality, so a caller's comparer can neither hide a repeat nor change which shard matches.
    private static Dictionary<ShardId, ShardRequestLimits> CopyLimits(
        IReadOnlyDictionary<ShardId, ShardRequestLimits> wireLimits,
        List<string> failures)
    {
        var copy = new Dictionary<ShardId, ShardRequestLimits>();
        foreach ((ShardId? key, ShardRequestLimits? value) in wireLimits)
        {
            if (key is null || value is null)
            {
                failures.Add("wireLimits: every entry needs a shard id and limits.");
            }
            else if (!copy.TryAdd(key, value))
            {
                failures.Add("wireLimits: a shard may appear only once.");
            }
        }

        return copy;
    }

    private static void CheckLimitsCoverShards(
        ShardRegistry registry,
        Dictionary<ShardId, ShardRequestLimits> limits,
        List<string> failures)
    {
        foreach (ShardDescriptor shard in registry.Shards)
        {
            if (!limits.ContainsKey(shard.Id))
            {
                failures.Add($"Federation:Shards:{shard.Ordinal}:WireLimits: is required.");
            }
        }

        if (limits.Keys.Any(key => !registry.TryGetById(key, out _)))
        {
            failures.Add("wireLimits: an entry matches no configured shard.");
        }
    }

    // Construction does no I/O; the constructor's ParamName says whether the root or a leaf was refused.
    private static MountedShardSecretResolver? BuildMount(
        string? root,
        IEnumerable<KeyValuePair<string, string>>? files,
        List<string> failures)
    {
        if (root is null)
        {
            failures.Add("secretMountRoot: is required for client credentials.");
        }

        if (files is null)
        {
            failures.Add("secretFiles: is required for client credentials.");
        }

        if (root is null || files is null)
        {
            return null;
        }

        try
        {
            return new MountedShardSecretResolver(root, files);
        }
        catch (ArgumentException ex)
        {
            failures.Add(ex.ParamName == "rootDirectory"
                ? "secretMountRoot: must be a fully qualified local directory path."
                : "secretFiles: every entry needs a non-blank, unique name and a plain, safe file name.");
            return null;
        }
    }

    private static void CheckAuth(ShardDescriptor shard, MountedShardSecretResolver? mounted, List<string> failures)
    {
        string path = $"Federation:Shards:{shard.Ordinal}:Auth";
        switch (shard.Auth)
        {
            case ManagedIdentityAuth managed:
                CheckResource(managed.Resource, path, failures);
                CheckClientId(managed.ClientId, path, failures);
                break;
            case ClientCredentialsAuth client:
                CheckResource(client.Resource, path, failures);
                CheckClientId(client.ClientId, path, failures);
                // The tenant goes into an authority path segment. A UUID or a DNS-like name is safe there.
                if (!char.IsAsciiLetterOrDigit(client.TenantId[0]) ||
                    !char.IsAsciiLetterOrDigit(client.TenantId[^1]) ||
                    client.TenantId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')))
                {
                    failures.Add($"{path}:TenantId: must be an alphanumeric identifier or domain name.");
                }

                if (mounted is not null && !mounted.Maps(client.SecretName))
                {
                    failures.Add($"{path}:SecretName: is not mapped by secretFiles.");
                }

                break;
        }
    }

    private static void CheckClientId(string? clientId, string path, List<string> failures)
    {
        if (clientId is not null && clientId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            failures.Add($"{path}:ClientId: must not contain white space or control characters.");
        }
    }

    private static void CheckResource(string resource, string path, List<string> failures)
    {
        if (!AzureShardCredentialProvider.IsValidResource(resource))
        {
            failures.Add($"{path}:Resource: must not contain white space or control characters or end in /.default.");
        }
    }

    // Managed-identity-only hosts supply no mount; any secret request is still refused rather than invented.
    private sealed class NoSecrets : IShardSecretResolver
    {
        public static readonly NoSecrets Instance = new();

        public ValueTask<string> ResolveAsync(string secretName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ShardCredentialException(ShardCredentialFailure.SecretUnavailable);
        }
    }
}

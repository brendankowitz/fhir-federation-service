using Azure.Core;
using Ignixa.Federation.Shards.Credentials;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// The SDK construction seam of <see cref="AzureShardCredentialProvider"/>: records exactly what each credential was
/// created with, so a test sees which identity, tenant, client and secret reached the SDK.
/// </summary>
internal sealed class RecordingCredentialFactory
{
    private readonly object _gate = new();
    private readonly List<CreatedManaged> _managed = [];
    private readonly List<CreatedClient> _clients = [];
    private int _created;

    /// <summary>Gets or sets a hook that configures each credential as it is created, before it is returned.</summary>
    public Action<RecordingTokenCredential>? OnCreate { get; set; }

    public IReadOnlyList<CreatedManaged> Managed
    {
        get
        {
            lock (_gate)
            {
                return [.. _managed];
            }
        }
    }

    public IReadOnlyList<CreatedClient> Clients
    {
        get
        {
            lock (_gate)
            {
                return [.. _clients];
            }
        }
    }

    public int CreatedCount
    {
        get
        {
            lock (_gate)
            {
                return _created;
            }
        }
    }

    public AzureShardCredentialProvider Provider(IShardSecretResolver secrets) => new(secrets, CreateManaged, CreateClient);

    public TokenCredential CreateManaged(string? clientId)
    {
        RecordingTokenCredential credential = NewCredential();
        lock (_gate)
        {
            _managed.Add(new CreatedManaged(clientId, credential));
        }

        return credential;
    }

    public TokenCredential CreateClient(string tenantId, string clientId, string secret)
    {
        RecordingTokenCredential credential = NewCredential();
        lock (_gate)
        {
            _clients.Add(new CreatedClient(tenantId, clientId, secret, credential));
        }

        return credential;
    }

    private RecordingTokenCredential NewCredential()
    {
        int id;
        lock (_gate)
        {
            id = ++_created;
        }

        var credential = new RecordingTokenCredential(id);
        OnCreate?.Invoke(credential);
        return credential;
    }

    internal sealed record CreatedManaged(string? ClientId, RecordingTokenCredential Credential);

    internal sealed record CreatedClient(string TenantId, string ClientId, string Secret, RecordingTokenCredential Credential);
}

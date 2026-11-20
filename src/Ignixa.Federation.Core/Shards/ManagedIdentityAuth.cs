namespace Ignixa.Federation.Core.Shards;

/// <summary>A managed identity, system-assigned or (with <see cref="ClientId"/>) user-assigned (design spec §10.2).</summary>
public sealed record ManagedIdentityAuth : ShardAuth
{
    internal ManagedIdentityAuth(string resource, string? clientId)
    {
        Resource = resource;
        ClientId = clientId;
    }

    /// <summary>Gets the token resource (audience) of the shard.</summary>
    public string Resource { get; }

    /// <summary>Gets the client id of a user-assigned identity, or <see langword="null"/> for the system-assigned one.</summary>
    public string? ClientId { get; }
}

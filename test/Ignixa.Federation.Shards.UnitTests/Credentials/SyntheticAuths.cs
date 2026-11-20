using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>Builds published shard auth the way production does: through <see cref="ShardRegistry"/>, never by hand.</summary>
internal static class SyntheticAuths
{
    private static readonly BackendProfile Profile = new("SyntheticProfile", SupportsPostSearch: true, MaxCount: 50, NextLinkRedemption.RepostToSearch, null);

    public static ManagedIdentityAuth Managed(string resource, string? clientId = null) =>
        (ManagedIdentityAuth)Publish(new AuthOptions { Type = "ManagedIdentity", Resource = resource, ClientId = clientId });

    public static ClientCredentialsAuth Client(string resource, string tenantId = "tenant-1", string clientId = "client-1", string secretName = "secret-1") =>
        (ClientCredentialsAuth)Publish(new AuthOptions
        {
            Type = "ClientCredentials",
            Resource = resource,
            TenantId = tenantId,
            ClientId = clientId,
            SecretName = secretName,
        });

    private static ShardAuth Publish(AuthOptions auth)
    {
        var options = new ShardOptions
        {
            Id = "east",
            Alias = "k7q2",
            BaseUrl = "https://fhir.synthetic.example/fhir",
            Profile = Profile.Name,
            Auth = auth,
        };
        return ShardRegistry.Create([options], [Profile]).Shards.Single().Auth;
    }
}

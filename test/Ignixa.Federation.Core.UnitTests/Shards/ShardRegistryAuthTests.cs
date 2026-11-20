using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardRegistryAuthTests
{
    private const string Sentinel = "S3NTINEL-must-not-be-echoed";

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    public void ManagedIdentity_NeedsResource_AndMayNameAClient(string? clientId, string? expectedClientId)
    {
        ShardAuth auth = Parse(new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir-east.internal", ClientId = clientId });

        ManagedIdentityAuth managed = auth.ShouldBeOfType<ManagedIdentityAuth>();
        managed.Resource.ShouldBe("https://fhir-east.internal");
        managed.ClientId.ShouldBe(expectedClientId);
    }

    [Fact]
    public void ClientCredentials_NeedsTenantClientResourceAndSecretName()
    {
        ShardAuth auth = Parse(new AuthOptions
        {
            Type = "ClientCredentials",
            TenantId = "tenant",
            ClientId = "client",
            Resource = "https://fhir-east.internal",
            SecretName = "fhir-east-secret",
        });

        ClientCredentialsAuth credentials = auth.ShouldBeOfType<ClientCredentialsAuth>();
        credentials.TenantId.ShouldBe("tenant");
        credentials.ClientId.ShouldBe("client");
        credentials.Resource.ShouldBe("https://fhir-east.internal");
        credentials.SecretName.ShouldBe("fhir-east-secret");
    }

    [Fact]
    public void MissingAuth_IsAnError()
    {
        ShardOptions shard = East();
        shard.Auth = null;

        SingleError("Federation:Shards:0:Auth", shard);
    }

    [Theory]
    [InlineData(Sentinel)]
    [InlineData("managedidentity")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownType_IsAnError_WithoutEchoingIt(string? type)
    {
        string error = SingleError("Federation:Shards:0:Auth:Type", WithAuth(new AuthOptions { Type = type, Resource = "https://fhir.example" }));

        error.ShouldNotContain(Sentinel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void ManagedIdentity_WithoutResource_IsAnError(string? resource)
    {
        SingleError("Federation:Shards:0:Auth:Resource", WithAuth(new AuthOptions { Type = "ManagedIdentity", Resource = resource }));
    }

    [Fact]
    public void ManagedIdentity_WithClientCredentialFields_IsAnError_WithoutEchoingThem()
    {
        IReadOnlyList<string> errors = Errors(WithAuth(new AuthOptions
        {
            Type = "ManagedIdentity",
            Resource = Sentinel,
            ClientId = Sentinel,
            TenantId = Sentinel,
            SecretName = Sentinel,
        }));

        errors.Count.ShouldBe(2);
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:0:Auth:TenantId: ", StringComparison.Ordinal));
        errors.ShouldContain(e => e.StartsWith("Federation:Shards:0:Auth:SecretName: ", StringComparison.Ordinal));
        errors.ShouldAllBe(e => !e.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedIdentity_ReportsEveryProblemAtOnce()
    {
        IReadOnlyList<string> errors = Errors(WithAuth(new AuthOptions { Type = "ManagedIdentity", TenantId = Sentinel, SecretName = Sentinel }));

        errors.Count.ShouldBe(3);
    }

    // Configuration often binds an unset field as an empty string; blank means absent, as it does for Require.
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ManagedIdentity_WithBlankClientCredentialFields_IsLegal(string blank)
    {
        Parse(new AuthOptions { Type = "ManagedIdentity", Resource = "https://fhir-east.internal", TenantId = blank, SecretName = blank })
            .ShouldBeOfType<ManagedIdentityAuth>();
    }

    [Theory]
    [InlineData("TenantId")]
    [InlineData("ClientId")]
    [InlineData("Resource")]
    [InlineData("SecretName")]
    public void ClientCredentials_MissingAField_IsAnError_WithoutEchoingTheOthers(string missing)
    {
        var auth = new AuthOptions
        {
            Type = "ClientCredentials",
            TenantId = missing == "TenantId" ? " " : Sentinel,
            ClientId = missing == "ClientId" ? null : Sentinel,
            Resource = missing == "Resource" ? string.Empty : Sentinel,
            SecretName = missing == "SecretName" ? null : Sentinel,
        };

        string error = SingleError($"Federation:Shards:0:Auth:{missing}", WithAuth(auth));

        error.ShouldNotContain(Sentinel);
    }

    [Fact]
    public void ClientCredentials_MissingEverything_ReportsEveryField()
    {
        IReadOnlyList<string> errors = Errors(WithAuth(new AuthOptions { Type = "ClientCredentials" }));

        errors.Count.ShouldBe(4);
    }

    private static ShardAuth Parse(AuthOptions auth) => Create(WithAuth(auth)).Shards[0].Auth;

    private static ShardOptions WithAuth(AuthOptions auth)
    {
        ShardOptions shard = East();
        shard.Auth = auth;
        return shard;
    }
}

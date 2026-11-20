using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Shards;
using Shouldly;
using static Ignixa.Federation.Core.UnitTests.Shards.TestShards;

namespace Ignixa.Federation.Core.UnitTests.Shards;

public sealed class ShardAuthTests
{
    [Fact]
    public void DerivingOutsideCore_FailsAtConstruction()
    {
        Should.Throw<InvalidOperationException>(() => new ForgedAuth());
    }

    [Fact]
    public void CopyingEitherVariant_StillWorks()
    {
        ShardOptions clientCredentials = West();
        clientCredentials.Auth = new AuthOptions
        {
            Type = "ClientCredentials",
            TenantId = "tenant",
            ClientId = "client",
            Resource = "https://west.example",
            SecretName = "west-secret",
        };
        ShardRegistry registry = Create(East(), clientCredentials);

        foreach (ShardDescriptor shard in registry.Shards)
        {
            ShardAuth copy = shard.Auth with { };

            copy.ShouldBe(shard.Auth);
            copy.ShouldNotBeSameAs(shard.Auth);
        }
    }

    /// <summary>The derivation the copy constructor would otherwise allow from another assembly.</summary>
    private sealed record ForgedAuth : ShardAuth
    {
        public ForgedAuth()
            : base((ShardAuth)null!)
        {
        }
    }
}

using Azure.Core;
using Azure.Identity;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// The production SDK credentials are built without any identity traffic: these tests inspect the options and the credential
/// types only, and never request a token.
/// </summary>
public sealed class AzureCredentialFactoryTests
{
    [Fact]
    public void ClientSecretOptions_PinThePublicCloudAuthorityAndTurnOffEverySdkDiagnostic()
    {
        ClientSecretCredentialOptions options = AzureCredentialFactory.ClientSecretOptions();

        AssertPinned(options);
        options.AdditionallyAllowedTenants.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("11111111-2222-3333-4444-555555555555")]
    public void ManagedIdentityOptions_PinThePublicCloudAuthorityAndTurnOffEverySdkDiagnostic(string? clientId)
    {
        AssertPinned(AzureCredentialFactory.ManagedIdentityOptions(clientId));
    }

    [Fact]
    public void ManagedIdentityIsTheRealSdkCredentialForBothVariants()
    {
        AzureCredentialFactory.ManagedIdentity(null).ShouldBeOfType<ManagedIdentityCredential>();
        AzureCredentialFactory.ManagedIdentity("11111111-2222-3333-4444-555555555555").ShouldBeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public void ClientSecretIsTheRealSdkCredential()
    {
        AzureCredentialFactory.ClientSecret("00000000-0000-0000-0000-000000000001", "synthetic-client", "synthetic-secret")
            .ShouldBeOfType<ClientSecretCredential>();
    }

    [Fact]
    public void TheSdkRefusesAMalformedTenantWhenTheCredentialIsBuilt()
    {
        Should.Throw<ArgumentException>(() => AzureCredentialFactory.ClientSecret("not a tenant!", "synthetic-client", "synthetic-secret"));
    }

    private static void AssertPinned(TokenCredentialOptions options)
    {
        options.AuthorityHost.ShouldBe(AzureAuthorityHosts.AzurePublicCloud);
        options.IsUnsafeSupportLoggingEnabled.ShouldBeFalse();
        options.Diagnostics.IsLoggingEnabled.ShouldBeFalse();
        options.Diagnostics.IsLoggingContentEnabled.ShouldBeFalse();
        options.Diagnostics.IsDistributedTracingEnabled.ShouldBeFalse();
        options.Diagnostics.IsAccountIdentifierLoggingEnabled.ShouldBeFalse();
    }
}

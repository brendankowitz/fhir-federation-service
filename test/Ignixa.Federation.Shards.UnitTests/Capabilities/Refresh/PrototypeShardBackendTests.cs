using System.Collections;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// The settings-to-backend composition is resolved from a generic host that is built but never started, so no metadata is
/// fetched and no token is requested.
/// </summary>
public sealed class PrototypeShardBackendTests
{
    private const string Sentinel = "zzSENTINELzz";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string IgnixaProfile => PrototypeBackendCatalog.Profiles[0].Name;

    private static string MicrosoftProfile => PrototypeBackendCatalog.Profiles[1].Name;

    [Fact]
    public void ManagedIdentityOnlyComposesTheCatalogRegistryWithNoSecretMountAndOneRouterAndRefresh()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile), Managed("west", MicrosoftProfile));
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(options, GenerousLimits("east", "west")).ShouldBeSameAs(services);

        using IHost host = Build(services);
        var registry = host.Services.GetRequiredService<ShardRegistry>();
        registry.Shards[0].Profile.ShouldBeSameAs(PrototypeBackendCatalog.Profiles[0]);
        registry.Shards[1].Profile.ShouldBeSameAs(PrototypeBackendCatalog.Profiles[1]);

        var router = host.Services.GetRequiredService<MultiShardHttpClient>();
        router.ShouldBeSameAs(host.Services.GetRequiredService<MultiShardHttpClient>());
        foreach (ShardDescriptor shard in registry.Shards)
        {
            router.GetClient(shard).IsFor(shard).ShouldBeTrue();
        }

        var refresher = host.Services.GetRequiredService<ShardCapabilityRefresher>();
        refresher.ShouldBeSameAs(host.Services.GetRequiredService<ShardCapabilityRefresher>());
        refresher.Current.Shards.Count.ShouldBe(2);
        Registrations<ShardRegistry>(services).ShouldBe(1);
        Registrations<MultiShardHttpClient>(services).ShouldBe(1);
        Registrations<ShardCapabilityRefresher>(services).ShouldBe(1);
        services.Count(d => d.ImplementationType == typeof(ShardCapabilityRefreshWorker)).ShouldBe(1);
    }

    [Fact]
    public void NoCredentialOrSecretServiceIsExposedAsAReplacementPoint()
    {
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(Federation(Managed("east", IgnixaProfile)), GenerousLimits("east"));

        Registrations<IShardCredentialProvider>(services).ShouldBe(0);
        Registrations<IShardSecretResolver>(services).ShouldBe(0);
    }

    [Fact]
    public void ManagedIdentityOnlyNeverEnumeratesOrChecksTheMountInputs()
    {
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(
            Federation(Managed("east", IgnixaProfile)),
            GenerousLimits("east"),
            secretMountRoot: "relative/not-a-root",
            secretFiles: new ThrowingFiles());

        using IHost host = Build(services);
        host.Services.GetRequiredService<MultiShardHttpClient>().ShouldNotBeNull();
    }

    [Fact]
    public void EachOwnerHasItsOwnWireCeiling()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile), Managed("west", MicrosoftProfile));
        var limits = new Dictionary<ShardId, ShardRequestLimits>
        {
            [ShardId.Parse("east")] = new(1, 1),
            [ShardId.Parse("west")] = SyntheticLimits.Generous,
        };
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(options, limits);

        using IHost host = Build(services);
        var router = host.Services.GetRequiredService<MultiShardHttpClient>();

        router.FitsSearchRequest(OneRowSearch("east")).ShouldBeFalse();
        router.FitsSearchRequest(OneRowSearch("west")).ShouldBeTrue();
    }

    [Fact]
    public void LimitKeysAreMatchedByShardIdEqualityWhateverComparerTheCallerUses()
    {
        var limits = new Dictionary<ShardId, ShardRequestLimits>(ReferenceEqualityComparer.Instance)
        {
            [ShardId.Parse("east")] = new(1, 1),
        };
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(Federation(Managed("east", IgnixaProfile)), limits);

        using IHost host = Build(services);

        host.Services.GetRequiredService<MultiShardHttpClient>().FitsSearchRequest(OneRowSearch("east")).ShouldBeFalse();
    }

    [Fact]
    public void RegistrationCapturesItsInputsSoLaterEditsHaveNoEffect()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        var limits = GenerousLimits("east");
        limits[ShardId.Parse("east")] = new ShardRequestLimits(1, 1);
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(options, limits);

        options.Shards[0].Id = "renamed";
        options.Shards.Add(Managed("extra", MicrosoftProfile, "https://extra.synthetic.example/fhir"));
        options.Timeouts.BackendCall = TimeSpan.Zero;
        options.Limits.MaxResponseBytes = 0;
        limits.Clear();

        using IHost host = Build(services);
        var registry = host.Services.GetRequiredService<ShardRegistry>();
        registry.Shards.Select(s => s.Id.Value).ShouldBe(["east"]);
        host.Services.GetRequiredService<MultiShardHttpClient>().FitsSearchRequest(OneRowSearch("east")).ShouldBeFalse();
    }

    [Fact]
    public async Task ClientCredentialsResolveFromTheMountOnlyWhenACallNeedsThemAndFailClosedWhenTheFileIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "ignixa-prototype-" + Guid.NewGuid().ToString("N"));
        FederationOptions options = Federation(Managed("east", IgnixaProfile), Client("west", MicrosoftProfile, "west-secret"));
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(options, GenerousLimits("east", "west"), root, [new("west-secret", "west.key")]);

        Directory.Exists(root).ShouldBeFalse();
        using IHost host = Build(services);
        var router = host.Services.GetRequiredService<MultiShardHttpClient>();
        router.ShouldNotBeNull();

        // The actual Azure provider asks its mounted resolver first; a missing leaf fails before any token request or send.
        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() =>
            router.SearchAsync(OneRowSearch("west"), Ct));

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void AnExistingGeneralPurposeBackendRegistrationIsStillRefusedAndLeftAlone()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(options, GenerousLimits("east"));
        int before = services.Count;

        Should.Throw<InvalidOperationException>(() => services.AddPrototypeShardBackend(options, GenerousLimits("east")));
        ShardRegistry registry = ShardRegistry.Create(options.Shards, PrototypeBackendCatalog.Profiles);
        Should.Throw<InvalidOperationException>(() =>
            services.AddShardBackend(registry, new SyntheticCredentials(), TimeSpan.FromSeconds(1), 1024, GenerousLimits("east")));

        services.Count.ShouldBe(before);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits = GenerousLimits("east");

        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddPrototypeShardBackend(options, limits));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddPrototypeShardBackend(null!, limits));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddPrototypeShardBackend(options, null!));
    }

    [Fact]
    public void AnInvalidProfileIsRefusedAtItsIndexWithoutEchoingIt()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile), Managed("west", Sentinel));

        OptionsValidationException ex = Refuse(options, GenerousLimits("east", "west"));

        AssertRefused(ex, "Federation:Shards:1:Profile");
    }

    [Fact]
    public void ASyntheticProfileThatIsNotInTheCatalogIsRefused()
    {
        OptionsValidationException ex = Refuse(
            Federation(Managed("east", ShardHarness.Follow.Name)), GenerousLimits("east"));

        AssertRefused(ex, "Federation:Shards:0:Profile");
    }

    [Fact]
    public void InvalidAuthIsRefusedWithoutEchoingTheSubmittedValues()
    {
        ShardOptions shard = Managed("east", IgnixaProfile);
        shard.Auth!.Type = Sentinel;

        AssertRefused(Refuse(Federation(shard), GenerousLimits("east")), "Federation:Shards:0:Auth:Type");
    }

    [Fact]
    public void InvalidShardIdentityAndAddressAreRefusedWithoutEchoingTheSubmittedValues()
    {
        ShardOptions shard = Managed("east", IgnixaProfile);
        shard.Id = Sentinel + " bad id";
        shard.Alias = Sentinel + " bad alias";
        shard.BaseUrl = "http://" + Sentinel + " bad url";

        OptionsValidationException ex = Refuse(Federation(shard), GenerousLimits("east"));

        AssertRefused(ex, "Federation:Shards:0:Alias");
        AssertRefused(ex, "Federation:Shards:0:BaseUrl");
    }

    [Theory]
    [InlineData("Managed", "api://" + Sentinel + " has space")]
    [InlineData("Managed", "api://" + Sentinel + "/.default")]
    [InlineData("Managed", "api://" + Sentinel + "\u0001")]
    [InlineData("Client", "api://" + Sentinel + " has space")]
    [InlineData("Client", "api://" + Sentinel + "/.DEFAULT")]
    public void AMalformedResourceIsRefusedForBothVariantsAtItsIndex(string variant, string resource)
    {
        ShardOptions bad = variant == "Managed"
            ? Managed("west", MicrosoftProfile)
            : Client("west", MicrosoftProfile, "west-secret");
        bad.Auth!.Resource = resource;
        FederationOptions options = Federation(Managed("east", IgnixaProfile), bad);

        OptionsValidationException ex = Refuse(options, GenerousLimits("east", "west"), Mount(), [new("west-secret", "west.key")]);

        AssertRefused(ex, "Federation:Shards:1:Auth:Resource");
    }

    [Theory]
    [InlineData("api://synthetic/")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    [InlineData("https://fhir.synthetic.example")]
    public void AnAudienceThatIsNotNecessarilyAUriIsAccepted(string resource)
    {
        ShardOptions shard = Managed("east", IgnixaProfile);
        shard.Auth!.Resource = resource;
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(Federation(shard), GenerousLimits("east"));

        Registrations<ShardRegistry>(services).ShouldBe(1);
    }

    [Theory]
    [InlineData(Sentinel + "!")]
    [InlineData(".")]
    [InlineData("..")]
    public void AMalformedTenantIsRefusedBeforeAnythingIsRegistered(string tenant)
    {
        ShardOptions shard = Client("east", IgnixaProfile, "east-secret");
        shard.Auth!.TenantId = tenant;

        OptionsValidationException ex = Refuse(Federation(shard), GenerousLimits("east"), Mount(), [new("east-secret", "east.key")]);

        AssertRefused(ex, "Federation:Shards:0:Auth:TenantId");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMalformedClientIdIsRefusedAtItsOwnPath(bool clientCredentials)
    {
        ShardOptions shard = clientCredentials
            ? Client("east", IgnixaProfile, "east-secret")
            : Managed("east", IgnixaProfile);
        shard.Auth!.ClientId = Sentinel + " invalid";

        OptionsValidationException ex = Refuse(Federation(shard), GenerousLimits("east"), Mount(), [new("east-secret", "east.key")]);

        AssertRefused(ex, "Federation:Shards:0:Auth:ClientId");
    }

    [Fact]
    public void ClientCredentialsWithoutAMountRootAreRefused()
    {
        FederationOptions options = Federation(Client("east", IgnixaProfile, "east-secret"));

        AssertRefused(Refuse(options, GenerousLimits("east"), root: null, [new("east-secret", "east.key")]), "secretMountRoot");
    }

    [Fact]
    public void ClientCredentialsWithoutAMappingAreRefused()
    {
        FederationOptions options = Federation(Client("east", IgnixaProfile, "east-secret"));

        AssertRefused(Refuse(options, GenerousLimits("east"), Mount(), files: null), "secretFiles");
    }

    [Theory]
    [InlineData("relative/" + Sentinel)]
    [InlineData("")]
    [InlineData("  ")]
    public void ClientCredentialsWithAnUnusableMountRootAreRefusedWithoutEchoingIt(string root)
    {
        FederationOptions options = Federation(Client("east", IgnixaProfile, "east-secret"));

        AssertRefused(Refuse(options, GenerousLimits("east"), root, [new("east-secret", "east.key")]), "secretMountRoot");
    }

    [Fact]
    public void ClientCredentialsWithAnUnsafeLeafAreRefusedWithoutEchoingIt()
    {
        FederationOptions options = Federation(Client("east", IgnixaProfile, "east-secret"));

        OptionsValidationException ex = Refuse(options, GenerousLimits("east"), Mount(), [new("east-secret", "../" + Sentinel)]);

        AssertRefused(ex, "secretFiles");
    }

    [Fact]
    public void ASecretNameMissingFromTheMapIsRefusedAtItsOwnShardByOrdinalComparison()
    {
        FederationOptions options = Federation(
            Client("east", IgnixaProfile, "east-secret"),
            Client("west", MicrosoftProfile, Sentinel + "-Secret"));

        OptionsValidationException ex = Refuse(
            options,
            GenerousLimits("east", "west"),
            Mount(),
            [new("east-secret", "east.key"), new(Sentinel + "-secret", "west.key")]);

        AssertRefused(ex, "Federation:Shards:1:Auth:SecretName");
        ex.Failures.ShouldNotContain(f => f.Contains("Shards:0:Auth:SecretName", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingWireLimitIsRefusedAtItsShard()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile), Managed("west", MicrosoftProfile));

        AssertRefused(Refuse(options, GenerousLimits("east")), "Federation:Shards:1:WireLimits");
    }

    [Fact]
    public void AnExtraWireLimitIsRefusedWithoutNamingIt()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        Dictionary<ShardId, ShardRequestLimits> limits = GenerousLimits("east");
        limits[ShardId.Parse("zzsentinelzz")] = SyntheticLimits.Generous;

        AssertRefused(Refuse(options, limits), "wireLimits");
    }

    [Fact]
    public void ANullWireLimitIsRefused()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        var limits = new Dictionary<ShardId, ShardRequestLimits> { [ShardId.Parse("east")] = null! };

        AssertRefused(Refuse(options, limits), "wireLimits");
    }

    [Fact]
    public void ARepeatedShardUnderADifferentComparerIsRefused()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        var limits = new Dictionary<ShardId, ShardRequestLimits>(ReferenceEqualityComparer.Instance)
        {
            [ShardId.Parse("east")] = SyntheticLimits.Generous,
            [ShardId.Parse("east")] = new(1, 1),
        };

        AssertRefused(Refuse(options, limits), "wireLimits");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4294967295)]
    public void ABackendCallDeadlineOutsideTheTransportBoundIsRefused(double milliseconds)
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        options.Timeouts.Request = TimeSpan.MaxValue;
        options.Timeouts.BackendCall = TimeSpan.FromMilliseconds(milliseconds);

        AssertRefused(Refuse(options, GenerousLimits("east")), "Federation:Timeouts:BackendCall");
    }

    [Theory]
    [InlineData(4294967294)]
    [InlineData(1)]
    public void ABackendCallDeadlineAtTheTransportBoundaryIsUsedByTheRouter(double milliseconds)
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        options.Timeouts.Request = TimeSpan.MaxValue;
        options.Timeouts.BackendCall = TimeSpan.FromMilliseconds(milliseconds);
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(options, GenerousLimits("east"));

        using IHost host = Build(services);

        host.Services.GetRequiredService<MultiShardHttpClient>().ShouldNotBeNull();
    }

    [Fact]
    public void ABackendCallDeadlineBeyondTheRequestDeadlineIsRefusedAndEqualIsAccepted()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        options.Timeouts.Request = TimeSpan.FromSeconds(9);
        options.Timeouts.BackendCall = TimeSpan.FromSeconds(10);

        AssertRefused(Refuse(options, GenerousLimits("east")), "Federation:Timeouts:BackendCall");

        options.Timeouts.Request = TimeSpan.FromSeconds(10);
        new ServiceCollection().AddPrototypeShardBackend(options, GenerousLimits("east"));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AResponseByteLimitThatIsNotPositiveIsRefused(long bytes)
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        options.Limits.MaxResponseBytes = bytes;

        AssertRefused(Refuse(options, GenerousLimits("east")), "Federation:Limits:MaxResponseBytes");
    }

    [Fact]
    public void AResponseByteLimitIsBoundedByTheLargestArrayAndTheBoundaryIsUsedByTheRouter()
    {
        FederationOptions options = Federation(Managed("east", IgnixaProfile));
        options.Limits.MaxResponseBytes = Array.MaxLength + 1L;
        AssertRefused(Refuse(options, GenerousLimits("east")), "Federation:Limits:MaxResponseBytes");

        options.Limits.MaxResponseBytes = Array.MaxLength;
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(options, GenerousLimits("east"));
        using IHost host = Build(services);

        host.Services.GetRequiredService<MultiShardHttpClient>().ShouldNotBeNull();
    }

    private static OptionsValidationException Refuse(
        FederationOptions options,
        IReadOnlyDictionary<ShardId, ShardRequestLimits> limits,
        string? root = null,
        IEnumerable<KeyValuePair<string, string>>? files = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Marker());
        OptionsValidationException ex = Should.Throw<OptionsValidationException>(() =>
            services.AddPrototypeShardBackend(options, limits, root, files));
        services.Count.ShouldBe(1);
        ex.InnerException.ShouldBeNull();
        return ex;
    }

    private static void AssertRefused(OptionsValidationException ex, string path)
    {
        ex.Failures.ShouldContain(f => f.StartsWith(path + ":", StringComparison.Ordinal));
        ex.Message.ShouldNotContain(Sentinel, Case.Insensitive);
        ex.Failures.ShouldAllBe(f => !f.Contains(Sentinel, StringComparison.OrdinalIgnoreCase));
    }

    private static IHost Build(IServiceCollection services)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        foreach (ServiceDescriptor descriptor in services)
        {
            builder.Services.Add(descriptor);
        }

        return builder.Build();
    }

    private static int Registrations<T>(IServiceCollection services) => services.Count(d => d.ServiceType == typeof(T));

    private static string Mount() => Path.Combine(Path.GetTempPath(), "ignixa-prototype-mount");

    private static FederationOptions Federation(params ShardOptions[] shards)
    {
        var options = new FederationOptions();
        foreach (ShardOptions shard in shards)
        {
            options.Shards.Add(shard);
        }

        return options;
    }

    private static Dictionary<ShardId, ShardRequestLimits> GenerousLimits(params string[] ids) =>
        ids.ToDictionary(ShardId.Parse, _ => SyntheticLimits.Generous);

    private static ShardOptions Managed(string id, string profile, string? baseUrl = null) => new()
    {
        Id = id,
        Alias = id,
        BaseUrl = baseUrl ?? $"https://{id}.synthetic.example/fhir",
        Profile = profile,
        Auth = new AuthOptions { Type = "ManagedIdentity", Resource = "api://synthetic" },
    };

    private static ShardOptions Client(string id, string profile, string secretName)
    {
        ShardOptions shard = Managed(id, profile);
        shard.Auth = new AuthOptions
        {
            Type = "ClientCredentials",
            Resource = "api://synthetic",
            TenantId = "00000000-0000-0000-0000-000000000001",
            ClientId = "synthetic-client",
            SecretName = secretName,
        };
        return shard;
    }

    private static ShardSearchRequest OneRowSearch(string shard) =>
        new(ShardId.Parse(shard), ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 1, ShardSearchMethod.Get);

    private sealed class Marker;

    private sealed class ThrowingFiles : IEnumerable<KeyValuePair<string, string>>
    {
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            throw new InvalidOperationException("The mount map was enumerated.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using System.Text;
using Ignixa.Federation.Core.Configuration;
using Ignixa.Federation.Core.Search;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Capabilities.Refresh;
using Ignixa.Federation.Shards.Credentials;
using Ignixa.Federation.Shards.Http;
using Ignixa.Federation.Shards.UnitTests.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Capabilities.Refresh;

/// <summary>
/// The <see cref="IConfigurationSection"/> overload reads only Shards-owned settings and hands them to the existing
/// composition. Hosts are built but never started, and every host is disposed. No token is requested and nothing leaves the
/// process; a bounded call to a shard whose mapped secret file is absent does make one local file check, which fails closed
/// before any credential or network step.
/// </summary>
public sealed class PrototypeShardBackendConfigurationTests
{
    private const string Sentinel = "zzSENTINELzz";
    private const string Generous = "1048576";

    // "/fhir/Patient?name=Sarah&_count=1": the origin-form target OneRowSearch sends to a synthetic shard.
    private const int OneRowTargetBytes = 33;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string IgnixaProfile => PrototypeBackendCatalog.Profiles[0].Name;

    private static string MicrosoftProfile => PrototypeBackendCatalog.Profiles[1].Name;

    [Fact]
    public void ManagedIdentityShardsComposeTheSharedBackendFromOneSection()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile);
        config["Shards:1:LinkBaseAliases:0"] = "https://alias.synthetic.example/fhir/";
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(config.Section()).ShouldBeSameAs(services);

        using IHost host = Build(services);
        var registry = host.Services.GetRequiredService<ShardRegistry>();
        registry.Shards.Select(s => s.Id.Value).ShouldBe(["east", "west"]);
        registry.Shards[0].Profile.ShouldBeSameAs(PrototypeBackendCatalog.Profiles[0]);
        registry.Shards[1].Profile.ShouldBeSameAs(PrototypeBackendCatalog.Profiles[1]);
        registry.Shards[1].CanonicalLinkBaseAliases.ShouldHaveSingleItem();
        host.Services.GetRequiredService<MultiShardHttpClient>().ShouldNotBeNull();
        host.Services.GetRequiredService<ShardCapabilityRefresher>().Current.Shards.Count.ShouldBe(2);
        Registrations<ShardRegistry>(services).ShouldBe(1);
        Registrations<MultiShardHttpClient>(services).ShouldBe(1);
        Registrations<IShardCredentialProvider>(services).ShouldBe(0);
    }

    [Fact]
    public void AnExistingRegistrationIsStillRefusedAndLeftAlone()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(config.Section());
        int before = services.Count;

        Should.Throw<InvalidOperationException>(() => services.AddPrototypeShardBackend(config.Section()));

        services.Count.ShouldBe(before);
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        IConfigurationSection section = new Cfg().Managed(0, "east", IgnixaProfile).Section();

        Should.Throw<ArgumentNullException>(() => ((IServiceCollection)null!).AddPrototypeShardBackend(section));
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddPrototypeShardBackend((IConfigurationSection)null!));
    }

    // Opposite small and large ceilings across owners, in both orders, so neither owner's nor either method's setting can be
    // swapped without a visible change. Both shards use client credentials with a mapped but absent file: a request that
    // fits reaches the credential step and fails closed, while one that does not fit is refused before it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachOwnerHasItsOwnGetCeilingAndAnOversizeRequestIsRefusedBeforeAnyCredentialCall(bool eastIsSmall)
    {
        string root = UnusedRoot();
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret").Client(1, "west", MicrosoftProfile, "west-secret");
        config.Mount(root, ("east-secret", "east.key"), ("west-secret", "west.key"));
        config.WireLimits(0, get: eastIsSmall ? "1" : Generous, post: eastIsSmall ? Generous : "1");
        config.WireLimits(1, get: eastIsSmall ? Generous : "1", post: eastIsSmall ? "1" : Generous);
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(config.Section());

        using IHost host = Build(services);
        var router = host.Services.GetRequiredService<MultiShardHttpClient>();
        string small = eastIsSmall ? "east" : "west";
        string large = eastIsSmall ? "west" : "east";

        router.FitsSearchRequest(OneRowSearch(small)).ShouldBeFalse();
        router.FitsSearchRequest(OneRowSearch(large)).ShouldBeTrue();
        ShardCallException refused = await Should.ThrowAsync<ShardCallException>(() => router.SearchAsync(OneRowSearch(small), Ct));
        refused.Failure.ShouldBe(ShardCallFailure.RequestTooLarge);
        ShardCredentialException reached = await Should.ThrowAsync<ShardCredentialException>(() => router.SearchAsync(OneRowSearch(large), Ct));
        reached.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void AGetCeilingEqualToTheTargetFitsAndOneByteShortDoesNot()
    {
        Cfg exact = new Cfg().Managed(0, "east", IgnixaProfile);
        exact.WireLimits(0, get: OneRowTargetBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), post: Generous);
        Cfg shortBy1 = new Cfg().Managed(0, "east", IgnixaProfile);
        shortBy1.WireLimits(0, get: (OneRowTargetBytes - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), post: Generous);

        using IHost exactHost = HostFor(exact);
        using IHost shortHost = HostFor(shortBy1);

        exactHost.Services.GetRequiredService<MultiShardHttpClient>().FitsSearchRequest(OneRowSearch("east")).ShouldBeTrue();
        shortHost.Services.GetRequiredService<MultiShardHttpClient>().FitsSearchRequest(OneRowSearch("east")).ShouldBeFalse();
    }

    // The prototype backends are GET only, so valid POST ceilings must not make POST search reachable through the router.
    [Fact]
    public async Task PostSearchStaysUnsupportedThroughTheRouterEvenWithValidConfiguredPostCeilings()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config.WireLimits(0, get: Generous, post: Generous);
        using IHost host = HostFor(config);
        var router = host.Services.GetRequiredService<MultiShardHttpClient>();
        var post = new ShardSearchRequest(ShardId.Parse("east"), ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 1, ShardSearchMethod.PostSearch);

        Should.Throw<ArgumentException>(() => router.FitsSearchRequest(post));
        await Should.ThrowAsync<ArgumentException>(() => router.SearchAsync(post, Ct));
    }

    // The prototype backends are GET only, so the POST ceiling has no observable router behavior; the reader's own value is pinned.
    [Fact]
    public void ThePostCeilingIsReadFromItsOwnSettingForItsOwnShard()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile);
        config.WireLimits(0, get: "11", post: "22");
        config.WireLimits(1, get: "33", post: "44");

        PrototypeShardBackendSettings settings = PrototypeShardBackendSettings.Read(config.Section());

        ShardRequestLimits east = settings.WireLimits[ShardId.Parse("east")];
        (east.MaxGetTargetBytes, east.MaxPostBodyBytes).ShouldBe((11, 22));
        ShardRequestLimits west = settings.WireLimits[ShardId.Parse("west")];
        (west.MaxGetTargetBytes, west.MaxPostBodyBytes).ShouldBe((33, 44));
    }

    [Fact]
    public void TheLargestInt32CeilingIsAcceptedAndALeadingZeroIsJustADigit()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config.WireLimits(0, get: "2147483647", post: "0010");

        PrototypeShardBackendSettings settings = PrototypeShardBackendSettings.Read(config.Section());

        ShardRequestLimits east = settings.WireLimits[ShardId.Parse("east")];
        (east.MaxGetTargetBytes, east.MaxPostBodyBytes).ShouldBe((int.MaxValue, 10));
    }

    [Theory]
    [InlineData("MaxGetTargetBytes", "")]
    [InlineData("MaxGetTargetBytes", "0")]
    [InlineData("MaxGetTargetBytes", " 5")]
    [InlineData("MaxGetTargetBytes", "5 ")]
    [InlineData("MaxGetTargetBytes", "+5")]
    [InlineData("MaxGetTargetBytes", "-5")]
    [InlineData("MaxGetTargetBytes", "5.0")]
    [InlineData("MaxGetTargetBytes", "1,000")]
    [InlineData("MaxGetTargetBytes", "0x10")]
    [InlineData("MaxGetTargetBytes", "\u0665")]
    [InlineData("MaxGetTargetBytes", "2147483648")]
    [InlineData("MaxGetTargetBytes", "99999999999999999999")]
    [InlineData("MaxGetTargetBytes", Sentinel)]
    [InlineData("MaxPostBodyBytes", "")]
    [InlineData("MaxPostBodyBytes", "0")]
    [InlineData("MaxPostBodyBytes", " 5")]
    [InlineData("MaxPostBodyBytes", "5 ")]
    [InlineData("MaxPostBodyBytes", "+5")]
    [InlineData("MaxPostBodyBytes", "-5")]
    [InlineData("MaxPostBodyBytes", "5.0")]
    [InlineData("MaxPostBodyBytes", "\u0665")]
    [InlineData("MaxPostBodyBytes", "2147483648")]
    [InlineData("MaxPostBodyBytes", Sentinel)]
    public void AMalformedWireCeilingIsRefusedAtItsOwnSettingWithoutEchoingIt(string field, string value)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile);
        config[$"Shards:1:WireLimits:{field}"] = value;

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, $"Federation:Shards:1:WireLimits:{field}");
        ex.Failures.ShouldNotContain(f => f.StartsWith("Federation:Shards:0:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("MaxGetTargetBytes")]
    [InlineData("MaxPostBodyBytes")]
    public void AMissingWireCeilingFieldIsRefusedAtItsOwnSetting(string field)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config.Remove($"Shards:0:WireLimits:{field}");

        AssertRefused(Refuse(config), $"Federation:Shards:0:WireLimits:{field}");
    }

    [Fact]
    public void AMissingWireLimitsGroupIsRefusedAtItsShard()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile);
        config.Remove("Shards:1:WireLimits:MaxGetTargetBytes");
        config.Remove("Shards:1:WireLimits:MaxPostBodyBytes");

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Shards:1:WireLimits");
        ex.Failures.ShouldNotContain(f => f.StartsWith("Federation:Shards:0:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Shards:0:WireLimits")]
    [InlineData("Shards:0:WireLimits:MaxGetTargetBytes")]
    [InlineData("Shards:0:WireLimits:MaxPostBodyBytes")]
    [InlineData("Shards:0")]
    [InlineData("Shards:0:Auth")]
    [InlineData("Shards:0:Id")]
    [InlineData("Shards:0:Auth:Type")]
    [InlineData("Shards:0:LinkBaseAliases")]
    [InlineData("Shards")]
    [InlineData("Timeouts")]
    [InlineData("Timeouts:BackendCall")]
    [InlineData("Limits")]
    [InlineData("Limits:MaxResponseBytes")]
    public void AScalarPlusChildrenMixedShapeIsRefusedAtTheSettingWithoutEchoingEitherValue(string key)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config[key] = Sentinel;
        config[key + ":Child"] = Sentinel;

        AssertRefused(Refuse(config), "Federation:" + key);
    }
    [Theory]
    [InlineData("Shards:0:WireLimits", "Federation:Shards:0:WireLimits")]
    [InlineData("Shards:0:Auth", "Federation:Shards:0:Auth")]
    [InlineData("Timeouts", "Federation:Timeouts")]
    [InlineData("Limits", "Federation:Limits")]
    public void AScalarWhereAGroupBelongsIsRefusedAtTheSetting(string key, string expectedPath)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        foreach (string nested in config.Keys.Where(k => k.StartsWith(key + ":", StringComparison.Ordinal)).ToArray())
        {
            config.Remove(nested);
        }

        config[key] = Sentinel;

        AssertRefused(Refuse(config), expectedPath);
    }

    [Theory]
    [InlineData("Shards:0:Id")]
    [InlineData("Shards:0:Alias")]
    [InlineData("Shards:0:BaseUrl")]
    [InlineData("Shards:0:Profile")]
    [InlineData("Shards:0:Auth:Type")]
    [InlineData("Shards:0:Auth:Resource")]
    [InlineData("Timeouts:BackendCall")]
    [InlineData("Limits:MaxResponseBytes")]
    [InlineData("Shards:0:WireLimits:MaxGetTargetBytes")]
    public void AGroupWhereAValueBelongsIsRefusedAtTheSetting(string key)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config.Remove(key);
        config[key + ":Child"] = Sentinel;

        AssertRefused(Refuse(config), "Federation:" + key);
    }

    [Theory]
    [InlineData("Shards", "Federation:Shards")]
    [InlineData("Shards:0:LinkBaseAliases", "Federation:Shards:0:LinkBaseAliases")]
    public void AnEntryKeyThatIsNotADecimalIndexIsRefusedAtItsListWithoutEchoingTheKey(string list, string expectedPath)
    {
        foreach (string badKey in new[] { Sentinel, "-1", "1.5", "1e2", "\u0665", "99999999999", " 1", "1 " })
        {
            Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
            config[$"{list}:{badKey}:Id"] = "east2";

            AssertRefused(Refuse(config), expectedPath);
        }
    }

    [Fact]
    public void ARepeatedNumericIndexIsRefusedAtItsList()
    {
        Cfg config = new Cfg().Managed(1, "east", IgnixaProfile);
        config["Shards:01:Id"] = "other";

        AssertRefused(Refuse(config), "Federation:Shards");
    }

    [Fact]
    public void IndexesWithGapsKeepNumericOrderAndTheRegistryReportsTheConfiguredIndex()
    {
        // 10 sorts before 2 lexically; the registry order must follow the number.
        Cfg ordered = new Cfg().Managed(10, "late", MicrosoftProfile).Managed(2, "early", IgnixaProfile);
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(ordered.Section());
        using (IHost host = Build(services))
        {
            host.Services.GetRequiredService<ShardRegistry>().Shards.Select(s => s.Id.Value).ShouldBe(["early", "late"]);
        }

        Cfg bad = new Cfg().Managed(3, "east", IgnixaProfile).Managed(9, "west", Sentinel);
        AssertRefused(Refuse(bad), "Federation:Shards:9:Profile");

        Cfg duplicate = new Cfg().Managed(3, "east", IgnixaProfile).Managed(9, "west", MicrosoftProfile);
        duplicate["Shards:9:BaseUrl"] = duplicate["Shards:3:BaseUrl"];
        OptionsValidationException ex = Refuse(duplicate);
        ex.Failures.ShouldContain(f => f.StartsWith("Federation:Shards:9:BaseUrl: duplicates Federation:Shards:3:BaseUrl", StringComparison.Ordinal));
        ex.Failures.ShouldAllBe(f => !f.Contains("Shards:0", StringComparison.Ordinal) && !f.Contains("Shards:1:", StringComparison.Ordinal));
    }

    [Fact]
    public void LinkBaseAliasIndexesWithGapsAreReportedAsConfigured()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config["Shards:0:LinkBaseAliases:0"] = "https://alias.synthetic.example/fhir";
        config["Shards:0:LinkBaseAliases:7"] = "http://" + Sentinel + ".example";

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Shards:0:LinkBaseAliases:7");
        ex.Failures.ShouldNotContain(f => f.StartsWith("Federation:Shards:0:LinkBaseAliases:1", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonCanonicalShardKeyIsReportedExactlyAsConfiguredAndStillOrderedNumerically()
    {
        // "010" sorts after "2" by value although it sorts before it as text.
        Cfg ordered = new Cfg().Managed(10, "late", MicrosoftProfile).Managed(2, "early", IgnixaProfile).Rekey("Shards:10", "Shards:010");
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(ordered.Section());
        using (IHost host = Build(services))
        {
            host.Services.GetRequiredService<ShardRegistry>().Shards.Select(s => s.Id.Value).ShouldBe(["early", "late"]);
        }

        // A reader refusal with no repeated index.
        Cfg reader = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile).Rekey("Shards:1", "Shards:01");
        reader["Shards:01:WireLimits:MaxGetTargetBytes"] = "many";
        OptionsValidationException readerRefusal = Refuse(reader);
        AssertRefused(readerRefusal, "Federation:Shards:01:WireLimits:MaxGetTargetBytes");
        readerRefusal.Failures.ShouldAllBe(f => !f.StartsWith("Federation:Shards:1:", StringComparison.Ordinal));
    }

    [Fact]
    public void ATranslatedRegistryRefusalNamesANonCanonicalShardKeyAsConfigured()
    {
        Cfg semantic = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", Sentinel).Rekey("Shards:1", "Shards:01");
        OptionsValidationException semanticRefusal = Refuse(semantic);
        AssertRefused(semanticRefusal, "Federation:Shards:01:Profile");
        semanticRefusal.Failures.ShouldAllBe(f => !f.StartsWith("Federation:Shards:1:", StringComparison.Ordinal));

        Cfg duplicate = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile).Rekey("Shards:1", "Shards:01");
        duplicate["Shards:01:BaseUrl"] = duplicate["Shards:0:BaseUrl"];
        OptionsValidationException duplicateRefusal = Refuse(duplicate);
        duplicateRefusal.Failures.ShouldContain(f => f.StartsWith("Federation:Shards:01:BaseUrl: duplicates Federation:Shards:0:BaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonCanonicalLinkBaseAliasKeyIsReportedExactlyAsConfigured()
    {
        Cfg config = new Cfg().Managed(1, "east", IgnixaProfile).Rekey("Shards:1", "Shards:01");
        config["Shards:01:LinkBaseAliases:00"] = "https://alias.synthetic.example/fhir";
        config["Shards:01:LinkBaseAliases:07"] = "http://" + Sentinel + ".example";

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Shards:01:LinkBaseAliases:07");
        ex.Failures.ShouldAllBe(f => !f.Contains("LinkBaseAliases:7", StringComparison.Ordinal) && !f.StartsWith("Federation:Shards:1:", StringComparison.Ordinal));
    }

    [Fact]
    public void ANonCanonicalSecretRowKeyIsReportedExactlyAsConfigured()
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config["Backend:SecretFiles:03:Name"] = "other";

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Backend:SecretFiles:03:FileName");
        ex.Failures.ShouldAllBe(f => !f.StartsWith("Federation:Backend:SecretFiles:3:", StringComparison.Ordinal));
    }

    [Fact]
    public void NumericallyEquivalentKeysAreStillARepeatedIndexWhateverTheirSpelling()
    {
        Cfg shards = new Cfg().Managed(1, "east", IgnixaProfile);
        shards["Shards:01:Id"] = "other";
        AssertRefused(Refuse(shards), "Federation:Shards");

        Cfg aliases = new Cfg().Managed(0, "east", IgnixaProfile);
        aliases["Shards:0:LinkBaseAliases:1"] = "https://one.synthetic.example/fhir";
        aliases["Shards:0:LinkBaseAliases:01"] = "https://other.synthetic.example/fhir";
        AssertRefused(Refuse(aliases), "Federation:Shards:0:LinkBaseAliases");

        Cfg files = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        files.Mount(UnusedRoot(), ("east-secret", "east.key"));
        files["Backend:SecretFiles:1:Name"] = "one";
        files["Backend:SecretFiles:1:FileName"] = "one.key";
        files["Backend:SecretFiles:01:Name"] = "other";
        files["Backend:SecretFiles:01:FileName"] = "other.key";
        AssertRefused(Refuse(files), "Federation:Backend:SecretFiles");
    }

    [Fact]
    public void ASemanticRefusalFromTheRegistrationIdentifiesTheSettingWithoutEchoingSubmittedValues()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "west", MicrosoftProfile);
        config["Shards:0:Auth:Type"] = Sentinel;
        config["Shards:1:Id"] = " " + Sentinel;
        config["Shards:1:BaseUrl"] = "http://" + Sentinel + " bad url";

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Shards:0:Auth:Type");
        AssertRefused(ex, "Federation:Shards:1:Id");
        AssertRefused(ex, "Federation:Shards:1:BaseUrl");
    }

    [Fact]
    public void ARepeatedShardIdIsLeftToTheRegistryAndNoCeilingIsOverwritten()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile).Managed(1, "other", MicrosoftProfile);
        config["Shards:1:Id"] = "east";
        config.WireLimits(1, get: "1", post: "1");

        OptionsValidationException ex = Refuse(config);

        ex.Failures.ShouldContain(f => f.StartsWith("Federation:Shards:1:Id: duplicates Federation:Shards:0:Id", StringComparison.Ordinal));
        PrototypeShardBackendSettings.Read(config.Section()).WireLimits[ShardId.Parse("east")].MaxGetTargetBytes
            .ShouldBe(int.Parse(Generous, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ARefusalReportsTheSectionPathItWasGiven()
    {
        Cfg config = new Cfg().Managed(0, "east", Sentinel);
        var services = new ServiceCollection();

        OptionsValidationException ex = Should.Throw<OptionsValidationException>(() =>
            services.AddPrototypeShardBackend(config.Section("App:Federation")));

        AssertRefused(ex, "App:Federation:Shards:0:Profile");
        services.Count.ShouldBe(0);
    }

    [Fact]
    public void UnrelatedAndInvalidSettingsOutsideTheShardsOwnedKeysAreIgnored()
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config["ProxyReads"] = "not-a-bool";
        config["Mode"] = Sentinel;
        config["Limits:MaxPageSize"] = "many";
        config["Limits:DefaultPageSize:Nested"] = "x";
        config["Timeouts:ChainSubquery"] = "never";
        config["Tokens:SigningKey"] = Sentinel;
        config["ChainResolution:Mode"] = Sentinel;
        config["Shards:0:Extra:Child"] = Sentinel;
        config["Shards:0:Auth:Unknown"] = Sentinel;
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(config.Section());

        Registrations<ShardRegistry>(services).ShouldBe(1);
    }

    [Fact]
    public void ManagedIdentityOnlyNeverTouchesTheMountSettingsAndClientCredentialsDo()
    {
        Cfg managed = new Cfg().Managed(0, "east", IgnixaProfile);
        managed["Backend:SecretMountRoot"] = "relative/not-a-root";
        managed["Backend:SecretFiles:oops:Name"] = "x";
        managed["Backend:SecretFiles:0:Name"] = "only-a-name";
        managed["Backend:SecretFiles:0:FileName:Nested"] = "x";

        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(managed.GuardedSection("Federation:Backend"));
        Registrations<ShardRegistry>(services).ShouldBe(1);

        // The guard is live: a client-credentials shard reads the same mount settings, so the provider throws.
        Cfg client = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        client.Mount(UnusedRoot(), ("east-secret", "east.key"));
        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddPrototypeShardBackend(client.GuardedSection("Federation:Backend")));
    }

    [Fact]
    public void ManagedIdentityIgnoresAMalformedMountEvenWhenInMemory()
    {
        Cfg managed = new Cfg().Managed(0, "east", IgnixaProfile);
        managed["Backend"] = "scalar-plus";
        managed["Backend:SecretMountRoot:Child"] = Sentinel;
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(managed.Section());

        Registrations<ShardRegistry>(services).ShouldBe(1);
    }

    [Fact]
    public async Task ClientSecretNamesKeepTheirCaseAreResolvedOnlyWhenACallNeedsThemAndFailClosed()
    {
        string root = UnusedRoot();
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "Secret").Client(1, "west", MicrosoftProfile, "secret");
        // Case-distinct rows both bind; numeric gaps are fine and keep their order.
        config.Mount(root, ("Secret", "upper.key"), ("secret", "lower.key"));
        config["Backend:SecretFiles:5:Name"] = "other";
        config["Backend:SecretFiles:5:FileName"] = "other.key";
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(config.Section());

        Directory.Exists(root).ShouldBeFalse();
        using IHost host = Build(services);
        var router = host.Services.GetRequiredService<MultiShardHttpClient>();
        foreach (string shard in new[] { "east", "west" })
        {
            ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() => router.SearchAsync(OneRowSearch(shard), Ct));
            failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        }

        Directory.Exists(root).ShouldBeFalse();
    }

    [Fact]
    public void ASecretNameThatDiffersOnlyByCaseFromTheMapIsRefusedAtItsShard()
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret").Client(1, "west", MicrosoftProfile, Sentinel + "-Secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"), (Sentinel + "-secret", "west.key"));

        OptionsValidationException ex = Refuse(config);

        AssertRefused(ex, "Federation:Shards:1:Auth:SecretName");
        ex.Failures.ShouldNotContain(f => f.Contains("Shards:0:Auth:SecretName", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingMountRootOrMapIsRefusedAtItsSetting()
    {
        Cfg noRoot = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        noRoot["Backend:SecretFiles:0:Name"] = "east-secret";
        noRoot["Backend:SecretFiles:0:FileName"] = "east.key";
        AssertRefused(Refuse(noRoot), "Federation:Backend:SecretMountRoot");

        Cfg noFiles = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        noFiles["Backend:SecretMountRoot"] = UnusedRoot();
        AssertRefused(Refuse(noFiles), "Federation:Backend:SecretFiles");
    }

    [Theory]
    [InlineData("relative/" + Sentinel)]
    [InlineData("")]
    [InlineData("  ")]
    public void AnUnusableMountRootIsRefusedAtItsSettingWithoutEchoingIt(string root)
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(root, ("east-secret", "east.key"));

        AssertRefused(Refuse(config), "Federation:Backend:SecretMountRoot");
    }

    [Theory]
    [InlineData("east-secret", "../" + Sentinel)]
    [InlineData("  ", "east.key")]
    [InlineData("", "east.key")]
    public void AnUnsafeOrBlankSecretMapEntryIsRefusedAtTheMapWithoutEchoingIt(string name, string leaf)
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config["Backend:SecretFiles:3:Name"] = name;
        config["Backend:SecretFiles:3:FileName"] = leaf;

        AssertRefused(Refuse(config), "Federation:Backend:SecretFiles");
    }

    [Fact]
    public void ARepeatedSecretNameAcrossGappedRowsIsRefusedAtTheMap()
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config["Backend:SecretFiles:4:Name"] = "east-secret";
        config["Backend:SecretFiles:4:FileName"] = "again.key";

        AssertRefused(Refuse(config), "Federation:Backend:SecretFiles");
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("FileName")]
    public void AMissingSecretMapFieldIsRefusedAtItsConfiguredRow(string field)
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config["Backend:SecretFiles:6:Name"] = "other";
        config["Backend:SecretFiles:6:FileName"] = "other.key";
        config.Remove($"Backend:SecretFiles:6:{field}");

        AssertRefused(Refuse(config), $"Federation:Backend:SecretFiles:6:{field}");
    }

    [Theory]
    [InlineData("Backend", "Federation:Backend")]
    [InlineData("Backend:SecretMountRoot", "Federation:Backend:SecretMountRoot")]
    [InlineData("Backend:SecretFiles", "Federation:Backend:SecretFiles")]
    [InlineData("Backend:SecretFiles:0", "Federation:Backend:SecretFiles:0")]
    [InlineData("Backend:SecretFiles:0:Name", "Federation:Backend:SecretFiles:0:Name")]
    public void AMalformedMountShapeIsRefusedAtItsSettingForClientCredentials(string key, string expectedPath)
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config[key] = Sentinel;
        config[key + ":Child"] = Sentinel;

        AssertRefused(Refuse(config), expectedPath);
    }

    [Fact]
    public void ASecretMapRowKeyThatIsNotAnIndexIsRefusedAtTheMapWithoutEchoingIt()
    {
        Cfg config = new Cfg().Client(0, "east", IgnixaProfile, "east-secret");
        config.Mount(UnusedRoot(), ("east-secret", "east.key"));
        config[$"Backend:SecretFiles:{Sentinel}:Name"] = "x";

        AssertRefused(Refuse(config), "Federation:Backend:SecretFiles");
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    [InlineData("10 seconds")]
    [InlineData(Sentinel)]
    public void AMalformedTimeoutIsRefusedAtItsSettingWithoutEchoingIt(string value)
    {
        foreach (string name in new[] { "BackendCall", "Request" })
        {
            Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
            config[$"Timeouts:{name}"] = value;

            AssertRefused(Refuse(config), $"Federation:Timeouts:{name}");
        }
    }

    [Fact]
    public void TimeoutsAreIndependentAndAbsentOnesKeepTheirDefaults()
    {
        // BackendCall alone keeps the 30 s default Request ceiling: 40 s is beyond it, 20 s within.
        Cfg beyond = new Cfg().Managed(0, "east", IgnixaProfile);
        beyond["Timeouts:BackendCall"] = "00:00:40";
        AssertRefused(Refuse(beyond), "Federation:Timeouts:BackendCall");

        Cfg within = new Cfg().Managed(0, "east", IgnixaProfile);
        within["Timeouts:BackendCall"] = "00:00:20";
        new ServiceCollection().AddPrototypeShardBackend(within.Section());

        // Request alone lowers the default 10 s call deadline's ceiling.
        Cfg lowered = new Cfg().Managed(0, "east", IgnixaProfile);
        lowered["Timeouts:Request"] = "00:00:05";
        AssertRefused(Refuse(lowered), "Federation:Timeouts:BackendCall");

        Cfg both = new Cfg().Managed(0, "east", IgnixaProfile);
        both["Timeouts:BackendCall"] = "00:01:30";
        both["Timeouts:Request"] = "00:02:00";
        new ServiceCollection().AddPrototypeShardBackend(both.Section());
    }

    [Theory]
    [InlineData("12x")]
    [InlineData("")]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    [InlineData(Sentinel)]
    public void AMalformedResponseByteLimitIsRefusedAtItsSettingWithoutEchoingIt(string value)
    {
        Cfg config = new Cfg().Managed(0, "east", IgnixaProfile);
        config["Limits:MaxResponseBytes"] = value;

        AssertRefused(Refuse(config), "Federation:Limits:MaxResponseBytes");
    }

    [Fact]
    public void AResponseByteLimitIsParsedAndBoundedByTheRegistration()
    {
        Cfg zero = new Cfg().Managed(0, "east", IgnixaProfile);
        zero["Limits:MaxResponseBytes"] = "0";
        AssertRefused(Refuse(zero), "Federation:Limits:MaxResponseBytes");

        Cfg over = new Cfg().Managed(0, "east", IgnixaProfile);
        over["Limits:MaxResponseBytes"] = (Array.MaxLength + 1L).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AssertRefused(Refuse(over), "Federation:Limits:MaxResponseBytes");

        Cfg ok = new Cfg().Managed(0, "east", IgnixaProfile);
        ok["Limits:MaxResponseBytes"] = Array.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        new ServiceCollection().AddPrototypeShardBackend(ok.Section());
    }

    [Theory]
    [InlineData("Timeouts", "BackendCall")]
    [InlineData("Timeouts", "Request")]
    [InlineData("Limits", "MaxResponseBytes")]
    public void AnExplicitJsonNullTransportValueIsRefusedInsteadOfDefaulted(string group, string field)
    {
        IConfigurationSection section = JsonSection($$"""
            "{{group}}": { "{{field}}": null },
            """);

        AssertRefused(Refuse(section), $"Federation:{group}:{field}");
    }

    [Fact]
    public void OmittedTransportLeavesAndOptionalClientIdStillUseTheirDefaultsWithJson()
    {
        IConfigurationSection section = JsonSection("""
            "Timeouts": { "ChainSubquery": null },
            "Limits": { "BackendCallsPerShard": null },
            """);
        var services = new ServiceCollection();

        services.AddPrototypeShardBackend(section);

        using IHost host = Build(services);
        var registry = host.Services.GetRequiredService<ShardRegistry>();
        registry.Shards[0].Auth.ShouldBeOfType<ManagedIdentityAuth>().ClientId.ShouldBeNull();
        host.Services.GetRequiredService<MultiShardHttpClient>().ShouldNotBeNull();
    }

    private static IConfigurationSection JsonSection(string transport)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($$"""
            {
                "Federation": {
                    {{transport}}
                    "Shards": [
                        {
                            "Id": "east",
                            "Alias": "east",
                            "BaseUrl": "https://east.synthetic.example/fhir",
                            "Profile": "{{IgnixaProfile}}",
                            "Auth": { "Type": "ManagedIdentity", "Resource": "api://{{Sentinel}}", "ClientId": null },
                            "WireLimits": { "MaxGetTargetBytes": 1024, "MaxPostBodyBytes": 1024 }
                        }
                    ]
                }
            }
            """));
        return new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection("Federation");
    }

    private static OptionsValidationException Refuse(Cfg config) => Refuse(config.Section());

    private static OptionsValidationException Refuse(IConfigurationSection section)
    {
        IServiceCollection services = new ServiceCollection();
        var marker = ServiceDescriptor.Singleton(new Marker());
        services.Add(marker);
        OptionsValidationException ex = Should.Throw<OptionsValidationException>(() =>
            services.AddPrototypeShardBackend(section));
        services.ShouldHaveSingleItem().ShouldBeSameAs(marker);
        ex.InnerException.ShouldBeNull();
        return ex;
    }

    private static void AssertRefused(OptionsValidationException ex, string path)
    {
        string all = string.Join(" | ", ex.Failures);
        ex.Failures.ShouldContain(f => f.StartsWith(path + ":", StringComparison.Ordinal), $"expected {path} in: {all}");
        ex.Message.ShouldNotContain(Sentinel, Case.Insensitive);
        ex.Failures.ShouldAllBe(f => !f.Contains(Sentinel, StringComparison.OrdinalIgnoreCase), $"echo in: {all}");
    }

    private static IHost HostFor(Cfg config)
    {
        var services = new ServiceCollection();
        services.AddPrototypeShardBackend(config.Section());
        return Build(services);
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

    private static string UnusedRoot() => Path.Combine(Path.GetTempPath(), "ignixa-prototype-config-" + Guid.NewGuid().ToString("N"));

    private static ShardSearchRequest OneRowSearch(string shard) =>
        new(ShardId.Parse(shard), ShardHarness.Patient, [new ParameterOccurrence("name", "Sarah")], 1, ShardSearchMethod.Get);

    private sealed class Marker;

    // Federation-relative settings; Section() roots them under the section name the host would pass.
    private sealed class Cfg : Dictionary<string, string?>
    {
        public Cfg()
            : base(StringComparer.OrdinalIgnoreCase)
        {
        }

        public Cfg Managed(int index, string id, string profile)
        {
            Identity(index, id, profile);
            this[$"Shards:{index}:Auth:Type"] = "ManagedIdentity";
            this[$"Shards:{index}:Auth:Resource"] = "api://synthetic";
            WireLimits(index, Generous, Generous);
            return this;
        }

        public Cfg Client(int index, string id, string profile, string secretName)
        {
            Identity(index, id, profile);
            this[$"Shards:{index}:Auth:Type"] = "ClientCredentials";
            this[$"Shards:{index}:Auth:Resource"] = "api://synthetic";
            this[$"Shards:{index}:Auth:TenantId"] = "00000000-0000-0000-0000-000000000001";
            this[$"Shards:{index}:Auth:ClientId"] = "synthetic-client";
            this[$"Shards:{index}:Auth:SecretName"] = secretName;
            WireLimits(index, Generous, Generous);
            return this;
        }

        public Cfg Rekey(string from, string to)
        {
            foreach (string key in Keys.Where(k => k.StartsWith(from + ":", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                string? value = this[key];
                Remove(key);
                this[to + key[from.Length..]] = value;
            }

            return this;
        }

        public void WireLimits(int index, string get, string post)
        {
            this[$"Shards:{index}:WireLimits:MaxGetTargetBytes"] = get;
            this[$"Shards:{index}:WireLimits:MaxPostBodyBytes"] = post;
        }

        public void Mount(string root, params (string Name, string FileName)[] files)
        {
            this["Backend:SecretMountRoot"] = root;
            for (int i = 0; i < files.Length; i++)
            {
                this[$"Backend:SecretFiles:{i}:Name"] = files[i].Name;
                this[$"Backend:SecretFiles:{i}:FileName"] = files[i].FileName;
            }
        }

        public IConfigurationSection Section(string name = "Federation") =>
            new ConfigurationBuilder().AddInMemoryCollection(Prefixed(name)).Build().GetSection(name);

        // A provider that throws if anything at or under the forbidden path is read.
        public IConfigurationSection GuardedSection(string forbiddenPath)
        {
            IConfigurationRoot root = new ConfigurationBuilder().Add(new GuardedSource(Prefixed("Federation"), forbiddenPath)).Build();
            return root.GetSection("Federation");
        }

        private Dictionary<string, string?> Prefixed(string name) => this.ToDictionary(p => $"{name}:{p.Key}", p => p.Value);

        private void Identity(int index, string id, string profile)
        {
            this[$"Shards:{index}:Id"] = id;
            this[$"Shards:{index}:Alias"] = id;
            this[$"Shards:{index}:BaseUrl"] = $"https://{id}.synthetic.example/fhir";
            this[$"Shards:{index}:Profile"] = profile;
        }
    }

    private sealed class GuardedSource(Dictionary<string, string?> data, string forbidden) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new GuardedProvider(data, forbidden);
    }

    private sealed class GuardedProvider(Dictionary<string, string?> data, string forbidden) : ConfigurationProvider
    {
        public override void Load() => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

        public override bool TryGet(string key, out string? value)
        {
            Guard(key);
            return base.TryGet(key, out value);
        }

        public override IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            Guard(parentPath);
            return base.GetChildKeys(earlierKeys, parentPath);
        }

        private void Guard(string? path)
        {
            if (path is not null && path.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The mount settings were read.");
            }
        }
    }
}

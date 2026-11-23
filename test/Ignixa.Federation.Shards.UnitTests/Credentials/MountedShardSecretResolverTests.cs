using System.Text;
using Ignixa.Federation.Core.Shards;
using Ignixa.Federation.Shards.Credentials;
using Shouldly;

namespace Ignixa.Federation.Shards.UnitTests.Credentials;

/// <summary>
/// The mounted-directory secret resolver: a synthetic temp directory stands in for the operator's read-only mount. No test
/// uses a real credential, special file or identity service.
/// </summary>
public sealed class MountedShardSecretResolverTests : IDisposable
{
    private const string Name = "secret-1";
    private const string Leaf = "shard-east.secret";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _root = Directory.CreateTempSubdirectory("mounted-secrets-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ReadsTheMappedLeafAndPreservesItsContentExactly()
    {
        Write(Leaf, "  synthetic-value  ");

        string value = await Resolver().ResolveAsync(Name, Ct);

        value.ShouldBe("  synthetic-value  ");
    }

    [Theory]
    [InlineData("tab\tinside and  double  spaces")]
    [InlineData("a\uFEFFb")]
    public async Task EveryOtherValueIsReturnedUnchanged(string content)
    {
        Write(Leaf, content);

        (await Resolver().ResolveAsync(Name, Ct)).ShouldBe(content);
    }

    [Fact]
    public async Task TheMapIsFrozenAtConstruction()
    {
        Write(Leaf, "synthetic-east");
        Write("other.secret", "synthetic-other");
        var source = new Dictionary<string, string>(StringComparer.Ordinal) { [Name] = Leaf };
        var resolver = new MountedShardSecretResolver(_root, source);

        source[Name] = "other.secret";
        source["added"] = "other.secret";
        source.Remove(Name);

        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-east");
        await AssertUnavailableAsync(resolver, "added");
    }

    [Theory]
    [InlineData("SECRET-1")]
    [InlineData(Leaf)]
    [InlineData("other.secret")]
    public async Task AnyNameThatIsNotAMappedNameIsUnavailableEvenWhenAFileExists(string name)
    {
        Write(Leaf, "synthetic-east");
        Write("other.secret", "synthetic-other");

        await AssertUnavailableAsync(Resolver(), name);
    }

    [Fact]
    public async Task AnUnmappedNameCannotReachAnExistingSiblingFile()
    {
        string mounted = Directory.CreateDirectory(Path.Combine(_root, "mounted")).FullName;
        Write("outside.secret", "synthetic-outside");
        var resolver = new MountedShardSecretResolver(mounted, [new(Name, Leaf)]);

        await AssertUnavailableAsync(resolver, Path.Combine("..", "outside.secret"));
        await AssertUnavailableAsync(resolver, Path.Combine(_root, "outside.secret"));
    }

    [Fact]
    public async Task AUnicodeNameIsComparedOrdinally()
    {
        Write(Leaf, "synthetic-east");
        var resolver = new MountedShardSecretResolver(_root, [new("é", Leaf)]);

        (await resolver.ResolveAsync("é", Ct)).ShouldBe("synthetic-east");
        await AssertUnavailableAsync(resolver, "e\u0301");
    }

    [Fact]
    public void ConstructionSnapshotsAnEnumerableOnceAndReadsNoFile()
    {
        int enumerations = 0;

        IEnumerable<KeyValuePair<string, string>> Files()
        {
            enumerations++;
            yield return new(Name, Leaf);
        }

        _ = new MountedShardSecretResolver(Path.Combine(_root, "does-not-exist"), Files());

        enumerations.ShouldBe(1);
    }

    [Fact]
    public async Task AFullyQualifiedRootIsNormalizedAndUsedForReads()
    {
        Write(Leaf, "synthetic-east");
        string unnormalized = _root + Path.DirectorySeparatorChar + "child" + Path.DirectorySeparatorChar + "..";

        var resolver = new MountedShardSecretResolver(unnormalized, [new(Name, Leaf)]);

        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-east");
    }

    [Fact]
    public void ANullRootOrMapIsANullArgumentError()
    {
        Should.Throw<ArgumentNullException>(() => new MountedShardSecretResolver(null!, [new(Name, Leaf)])).ParamName.ShouldBe("rootDirectory");
        Should.Throw<ArgumentNullException>(() => new MountedShardSecretResolver(_root, null!)).ParamName.ShouldBe("files");
    }

    [Fact]
    public void ARootWithAControlCharacterIsRefused()
    {
        string root = Path.Combine(_root, "a\nb");

        Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(root, [new(Name, Leaf)])).ParamName.ShouldBe("rootDirectory");
    }

    [Theory]
    [InlineData("\\\\server\\share\\dir")]
    [InlineData("//server/share/dir")]
    [InlineData("\\\\?\\C:\\dir")]
    [InlineData("\\\\.\\C:\\dir")]
    public void AWindowsNetworkOrDevicePathRootIsRefused(string root)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC and device roots are Windows path forms.");

        Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(root, [new(Name, Leaf)])).ParamName.ShouldBe("rootDirectory");
    }

    [Fact]
    public void ADuplicateNameIsRefused()
    {
        ArgumentException failure = Should.Throw<ArgumentException>(
            () => new MountedShardSecretResolver(_root, [new("dup-name-91", "a.secret"), new("dup-name-91", "b.secret")]));

        failure.ParamName.ShouldBe("files");
        failure.Message.ShouldNotContain("dup-name-91");
    }

    [Fact]
    public void ANullNameOrLeafIsRefused()
    {
        Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new(null!, Leaf)])).ParamName.ShouldBe("files");
        Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new(Name, null!)])).ParamName.ShouldBe("files");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/abs")]
    [InlineData("C:\\abs")]
    [InlineData("C:abs")]
    [InlineData("../up")]
    [InlineData("..\\up")]
    [InlineData("a:stream")]
    [InlineData("a.secret.")]
    [InlineData("a.secret ")]
    [InlineData("con.txt")]
    [InlineData("nul.tar.gz")]
    [InlineData("com9.txt")]
    [InlineData("LPT1")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a*b")]
    [InlineData("PROGRA~1")]
    public void AnUnsafeLeafIsRefused(string leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentException failure = Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new(Name, leaf)]));

        failure.ParamName.ShouldBe("files");
    }

    [Fact]
    public void ALeafLongerThanTheFilenameLimitIsRefused()
    {
        Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new(Name, new string('a', 256))])).ParamName.ShouldBe("files");
    }

    [Fact]
    public void RejectedInputsAreNeverQuotedInArgumentDiagnostics()
    {
        string[] markers = ["root-marker-71", "name-marker-72", "leaf-marker-73"];

        ArgumentException root = Should.Throw<ArgumentException>(() => new MountedShardSecretResolver("root-marker-71", [new(Name, Leaf)]));
        ArgumentException leaf = Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new("name-marker-72", "leaf-marker-73/bad")]));
        ArgumentException blank = Should.Throw<ArgumentException>(() => new MountedShardSecretResolver(_root, [new(" ", "leaf-marker-73")]));
        root.ParamName.ShouldBe("rootDirectory");
        leaf.ParamName.ShouldBe("files");
        blank.ParamName.ShouldBe("files");

        foreach (ArgumentException failure in new[] { root, leaf, blank })
        {
            failure.InnerException.ShouldBeNull();
            foreach (string marker in markers)
            {
                failure.Message.ShouldNotContain(marker);
                failure.ToString().ShouldNotContain(marker);
            }
        }
    }

    [Fact]
    public async Task AValueOfExactlyTheByteLimitIsAccepted()
    {
        string value = new('x', 4096);
        Write(Leaf, value);

        (await Resolver().ResolveAsync(Name, Ct)).ShouldBe(value);
    }

    [Fact]
    public async Task AValueOneByteOverTheLimitIsRefused()
    {
        Write(Leaf, new string('x', 4097));

        await AssertUnavailableAsync(Resolver(), Name);
    }

    [Fact]
    public async Task TheLimitCountsBytesNotCharacters()
    {
        // 1365 three-byte characters plus one ASCII byte is exactly 4096 bytes; one more euro sign is 4098 bytes in 1366 chars.
        string atLimit = new string('€', 1365) + "a";
        Write(Leaf, atLimit);
        (await Resolver().ResolveAsync(Name, Ct)).ShouldBe(atLimit);

        string overLimit = new('€', 1366);
        Utf8.GetByteCount(overLimit).ShouldBeGreaterThan(4096);
        overLimit.Length.ShouldBeLessThan(4096);
        Write(Leaf, overLimit);
        await AssertUnavailableAsync(Resolver(), Name);
    }

    public static TheoryData<string, byte[]> MalformedValues() => new()
    {
        { "empty", [] },
        { "whitespace only", " \t\u00a0\u2003 "u8.ToArray() },
        { "BOM then text", [0xEF, 0xBB, 0xBF, .. "synthetic"u8] },
        { "embedded NUL", [.. "syn"u8, 0x00, .. "thetic"u8] },
        { "trailing LF", "synthetic\n"u8.ToArray() },
        { "embedded CR", "syn\rthetic"u8.ToArray() },
        { "embedded LF", "syn\nthetic"u8.ToArray() },
        { "truncated sequence", [.. "synthetic"u8, 0xE2, 0x82] },
    };

    [Theory]
    [MemberData(nameof(MalformedValues))]
    public async Task AMalformedValueIsUnavailableWithNoDetail(string description, byte[] content)
    {
        description.ShouldNotBeNull();
        await File.WriteAllBytesAsync(Path.Combine(_root, Leaf), content, Ct);

        await AssertUnavailableAsync(Resolver(), Name);
    }

    [Fact]
    public async Task AMissingLeafIsUnavailable()
    {
        await AssertUnavailableAsync(Resolver(), Name);
    }

    [Fact]
    public async Task AMissingRootIsUnavailable()
    {
        var resolver = new MountedShardSecretResolver(Path.Combine(_root, "absent"), [new(Name, Leaf)]);

        await AssertUnavailableAsync(resolver, Name);
    }

    [Fact]
    public async Task ADirectoryWhereTheLeafShouldBeIsUnavailable()
    {
        Directory.CreateDirectory(Path.Combine(_root, Leaf));

        await AssertUnavailableAsync(Resolver(), Name);
    }

    [Fact]
    public async Task ASymbolicLinkLeafIsUnavailable()
    {
        Write("real.secret", "synthetic-real");
        string link = Path.Combine(_root, Leaf);
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_root, "real.secret"));
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && ex.HResult == unchecked((int)0x80070522))
        {
            Assert.Skip("This account cannot create symbolic links.");
        }

        await AssertUnavailableAsync(Resolver(), Name);
    }

    [Fact]
    public async Task AnUnreadableLeafIsUnavailableEvenAfterAnEarlierSuccess()
    {
        Write(Leaf, "synthetic-east");
        MountedShardSecretResolver resolver = Resolver();
        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-east");

        using (new FileStream(Path.Combine(_root, Leaf), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await AssertUnavailableAsync(resolver, Name);
        }

        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-east");
    }

    [Fact]
    public async Task ADeletedLeafIsNotServedFromAnEarlierSuccess()
    {
        Write(Leaf, "synthetic-east");
        MountedShardSecretResolver resolver = Resolver();
        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-east");

        File.Delete(Path.Combine(_root, Leaf));

        await AssertUnavailableAsync(resolver, Name);
    }

    [Fact]
    public async Task AMalformedReplacementIsNotMaskedByAnEarlierSuccessAndTheNextGoodOneRecovers()
    {
        Write(Leaf, "synthetic-one");
        MountedShardSecretResolver resolver = Resolver();
        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-one");

        Replace(Leaf, "synthetic\ntwo");
        await AssertUnavailableAsync(resolver, Name);

        Replace(Leaf, "synthetic-three");
        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-three");
    }

    [Fact]
    public async Task ASameDirectoryAtomicReplacementIsVisibleToTheNextLookup()
    {
        Write(Leaf, "synthetic-one");
        MountedShardSecretResolver resolver = Resolver();
        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-one");

        Replace(Leaf, "synthetic-two");

        (await resolver.ResolveAsync(Name, Ct)).ShouldBe("synthetic-two");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailureHasAFixedMessageAndNoInnerExceptionOrData(bool malformed)
    {
        if (malformed)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, Leaf), [.. "synthetic-secret-71"u8, 0xFF], Ct);
        }

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() => Resolver().ResolveAsync(Name, Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        failure.Message.ShouldBe(new ShardCredentialException(ShardCredentialFailure.SecretUnavailable).Message);
        failure.InnerException.ShouldBeNull();
        failure.Data.Count.ShouldBe(0);
        failure.HelpLink.ShouldBeNull();
        failure.ToString().ShouldNotContain("synthetic-secret-71");
        failure.ToString().ShouldNotContain(_root);
        failure.ToString().ShouldNotContain(Leaf);
        failure.ToString().ShouldNotContain(Name);
    }

    [Fact]
    public async Task EachFailureIsAFreshException()
    {
        MountedShardSecretResolver resolver = Resolver();

        ShardCredentialException first = await Should.ThrowAsync<ShardCredentialException>(() => resolver.ResolveAsync(Name, Ct).AsTask());
        ShardCredentialException second = await Should.ThrowAsync<ShardCredentialException>(() => resolver.ResolveAsync(Name, Ct).AsTask());

        second.ShouldNotBeSameAs(first);
    }

    [Theory]
    [InlineData(Name, true)]
    [InlineData(Name, false)]
    [InlineData("unknown", false)]
    public async Task ACanceledTokenWinsOverEveryOutcomeAndKeepsItsToken(string name, bool leafExists)
    {
        if (leafExists)
        {
            Write(Leaf, "synthetic-east");
        }

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        OperationCanceledException cancellation = await Should.ThrowAsync<OperationCanceledException>(
            () => Resolver().ResolveAsync(name, canceled.Token).AsTask());

        cancellation.CancellationToken.ShouldBe(canceled.Token);
    }

    [Fact]
    public async Task ACanceledTokenWinsOverAMalformedValue()
    {
        Write(Leaf, "bad\nvalue");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Resolver().ResolveAsync(Name, canceled.Token).AsTask());
    }

    [Fact]
    public async Task TheProviderExchangesTheExactFileValueThroughTheSdkSeam()
    {
        Write("client.secret", "  synthetic-client-secret  ");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new MountedShardSecretResolver(_root, [new("secret-1", "client.secret")]));

        string token = await provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), Ct);

        sdk.Clients.Single().Secret.ShouldBe("  synthetic-client-secret  ");
        token.ShouldBe(sdk.Clients.Single().Credential.Token);
    }

    [Fact]
    public async Task AMissingSecretCreatesNoSdkCredentialAndAcquiresNoToken()
    {
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new MountedShardSecretResolver(_root, [new("secret-1", "client.secret")]));

        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        sdk.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ACanceledProviderCallCreatesNoSdkCredentialAndAcquiresNoToken()
    {
        Write("client.secret", "synthetic-client-secret");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new MountedShardSecretResolver(_root, [new("secret-1", "client.secret")]));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(SyntheticAuths.Client("api://synthetic-shard"), canceled.Token).AsTask());

        sdk.CreatedCount.ShouldBe(0);
    }

    [Fact]
    public async Task ARotatedFileReachesTheSdkOnTheNextAcquisitionAndAFailureNeverReusesTheOldOne()
    {
        Write("client.secret", "synthetic-secret-one");
        var sdk = new RecordingCredentialFactory();
        AzureShardCredentialProvider provider = sdk.Provider(new MountedShardSecretResolver(_root, [new("secret-1", "client.secret")]));
        ShardAuth auth = SyntheticAuths.Client("api://synthetic-shard");
        await provider.GetAccessTokenAsync(auth, Ct);

        Replace("client.secret", "synthetic-secret-two");
        await provider.GetAccessTokenAsync(auth, Ct);
        File.Delete(Path.Combine(_root, "client.secret"));
        await Should.ThrowAsync<ShardCredentialException>(() => provider.GetAccessTokenAsync(auth, Ct).AsTask());

        sdk.Clients.Select(c => c.Secret).ShouldBe(["synthetic-secret-one", "synthetic-secret-two"]);
    }

    private MountedShardSecretResolver Resolver() => new(_root, [new(Name, Leaf)]);

    private void Write(string leaf, string content) => File.WriteAllText(Path.Combine(_root, leaf), content, Utf8);

    // The documented operator publication: a complete sibling file replaced over the leaf in the same directory
    // (File.Replace; on Windows a plain overwriting move is refused while any lookup has the file open).
    private void Replace(string leaf, string content)
    {
        string staged = Path.Combine(_root, $"staged-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(staged, content, Utf8);
        File.Replace(staged, Path.Combine(_root, leaf), destinationBackupFileName: null);
    }

    private static async Task AssertUnavailableAsync(MountedShardSecretResolver resolver, string name)
    {
        ShardCredentialException failure = await Should.ThrowAsync<ShardCredentialException>(() => resolver.ResolveAsync(name, Ct).AsTask());

        failure.Failure.ShouldBe(ShardCredentialFailure.SecretUnavailable);
        failure.Message.ShouldBe(new ShardCredentialException(ShardCredentialFailure.SecretUnavailable).Message);
        failure.InnerException.ShouldBeNull();
        failure.Data.Count.ShouldBe(0);
    }
}

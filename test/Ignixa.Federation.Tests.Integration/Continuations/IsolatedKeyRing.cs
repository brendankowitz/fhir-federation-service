using Microsoft.AspNetCore.DataProtection;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// A disposable Data Protection key ring in its own temporary directory. Every provider created over it is an
/// independent instance (its own services and key cache) that reads the same key files, as separate federation
/// instances sharing a key ring would.
/// </summary>
internal sealed class IsolatedKeyRing : IDisposable
{
    /// <summary>The application discriminator shared by the "instances" of one deployment in these tests.</summary>
    public const string ApplicationName = "Ignixa.Federation.Tests.Continuations";

    private readonly DirectoryInfo _directory;

    public IsolatedKeyRing()
    {
        _directory = Directory.CreateTempSubdirectory("ignixa-continuation-keys-");

        // Materialise the default key before any provider under test loads the ring, so every provider over this
        // directory resolves the same default key instead of racing to create its own.
        CreateProvider().CreateProtector("Ignixa.Federation.Tests.KeyRingSeed").Protect([0]);
    }

    /// <summary>Creates an independent provider over this ring's key files.</summary>
    /// <param name="applicationName">The application discriminator; a different value isolates the payloads.</param>
    /// <returns>A new provider.</returns>
    public IDataProtectionProvider CreateProvider(string applicationName = ApplicationName) =>
        DataProtectionProvider.Create(_directory, builder => builder.SetApplicationName(applicationName));

    public void Dispose() => _directory.Delete(recursive: true);
}

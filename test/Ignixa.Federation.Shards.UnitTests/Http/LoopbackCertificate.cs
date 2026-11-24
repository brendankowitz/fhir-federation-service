using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ignixa.Federation.Shards.UnitTests.Http;

/// <summary>
/// A self-signed leaf for <c>127.0.0.1</c> generated once per test run. The client under test trusts only this leaf, through
/// its internal test seam; nothing is added to an OS trust store.
/// </summary>
internal static class LoopbackCertificate
{
    private static readonly Lazy<X509Certificate2> s_server = new(Generate);

    /// <summary>Gets the leaf with its private key, for the server side of the handshake.</summary>
    public static X509Certificate2 Server => s_server.Value;

    /// <summary>Gets the leaf without its private key, for the client's custom trust store.</summary>
    public static X509Certificate2 Trust { get; } = X509CertificateLoader.LoadCertificate(Server.RawData);

    private static X509Certificate2 Generate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=loopback.synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));

        // SChannel needs a key it can reach outside this process's ephemeral key container, so round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), password: null);
    }
}

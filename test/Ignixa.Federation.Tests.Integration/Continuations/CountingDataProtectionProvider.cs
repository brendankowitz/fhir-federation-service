using Microsoft.AspNetCore.DataProtection;

namespace Ignixa.Federation.Tests.Integration.Continuations;

/// <summary>
/// Passes every call to a real provider and counts the cryptographic operations, so a test can show that bounded
/// or refused input never reached Data Protection.
/// </summary>
internal sealed class CountingDataProtectionProvider(IDataProtectionProvider inner) : IDataProtectionProvider
{
    public int ProtectCalls { get; private set; }

    public int UnprotectCalls { get; private set; }

    public IDataProtector CreateProtector(string purpose) => new CountingProtector(this, inner.CreateProtector(purpose));

    private sealed class CountingProtector(CountingDataProtectionProvider owner, IDataProtector inner) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => new CountingProtector(owner, inner.CreateProtector(purpose));

        public byte[] Protect(byte[] plaintext)
        {
            owner.ProtectCalls++;
            return inner.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            owner.UnprotectCalls++;
            return inner.Unprotect(protectedData);
        }
    }
}
